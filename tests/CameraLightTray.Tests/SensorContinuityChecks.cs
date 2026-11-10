using CameraLightTray.App.Platform;
using CameraLightTray.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CameraLightTray.Tests;

internal static class SensorContinuityChecks
{
    internal static int Run()
    {
        int passed = 0;
        var start = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var process = new ProcessIdentity(42, start.AddMinutes(-1).ToFileTime(), 1, "fixture", "example", "Example");
        CameraDevice[] cameras = [new("one", "Camera"), new("two", "Second camera")];
        SensorActivity Activity(string id, bool active, double second) =>
            new(id, "Camera", [new(42, active, start.AddSeconds(second).ToFileTime())]);
        MonitorSnapshot Snapshot(SensorObservations state, double second, string[]? selected = null, string? unavailable = null,
            ProcessIdentity? identity = null) =>
            state.Snapshot([identity ?? process], [], cameras, selected ?? ["one"], start.AddSeconds(second), unavailable);
        (SensorObservations State, long Primary) Ready()
        {
            var state = new SensorObservations(NullLogger.Instance);
            long primary = state.Begin(start);
            state.Receive(primary, [Activity("one", false, 0)], start);
            if (!Snapshot(state, 0).Healthy) throw new InvalidOperationException("Fixture is not ready.");
            return (state, primary);
        }
        void Check(string name, Action test) { test(); Console.WriteLine("PASS " + name); passed++; }
        static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        Check("warm handover never interrupts primary activation and preserves debounce continuity", () =>
        {
            var (state, primary) = Ready();
            Assert(state.NeedsSource(start.AddMinutes(1)), "Verification not scheduled.");
            long candidate = state.Begin(start.AddSeconds(60));
            var settings = new Settings { AutomationEnabled = true, ActivationMilliseconds = 500, Apps = [new("example", "Example", true)] };
            var decision = new AutomationDecision();
            state.Receive(primary, [Activity("one", true, 60)], start.AddSeconds(60));
            Assert(!decision.Update(settings, Snapshot(state, 60), ManualMode.Automatic, start.AddSeconds(60)), "Debounce was bypassed.");
            state.Receive(candidate, [Activity("one", true, 60)], start.AddSeconds(60.3));
            var switched = Snapshot(state, 60.3);
            Assert(switched.Healthy && state.Primary == candidate && switched.Epoch == 1, "Warm promotion discarded continuity.");
            Assert(decision.Update(settings, Snapshot(state, 60.5), ManualMode.Automatic, start.AddSeconds(60.5)), "Handover reset debounce.");
            Assert(!state.Receive(primary, [Activity("one", false, 61)], start.AddSeconds(61)), "Retired callback was accepted.");
            Assert(Snapshot(state, 61).Apps.Single().Status == CameraStatus.Active, "Late retired stop erased activity.");
        });
        Check("camera start and stop remain authoritative while a replacement takes ten seconds to warm", () =>
        {
            var (state, primary) = Ready();
            long candidate = state.Begin(start.AddSeconds(60));
            state.Receive(candidate, [], start.AddSeconds(61));
            state.Receive(primary, [Activity("one", true, 62)], start.AddSeconds(62));
            Assert(Snapshot(state, 62).Apps.Single().Status == CameraStatus.Active && state.Primary == primary, "Warm-up blocked start.");
            state.Receive(primary, [Activity("one", false, 65)], start.AddSeconds(65));
            Assert(Snapshot(state, 65).Healthy && Snapshot(state, 65).Apps.Single().Status == CameraStatus.Inactive, "Warm-up blocked stop.");
            state.Receive(candidate, [Activity("one", false, 65)], start.AddSeconds(70));
            Assert(Snapshot(state, 70).Healthy && state.Primary == candidate, "Complete candidate was not promoted.");
        });
        Check("older replacement reports cannot overwrite a more recent primary transition", () =>
        {
            var (state, primary) = Ready();
            long candidate = state.Begin(start.AddSeconds(60));
            state.Receive(primary, [Activity("one", true, 62)], start.AddSeconds(62));
            state.Receive(candidate, [Activity("one", false, 61)], start.AddSeconds(63));
            Assert(Snapshot(state, 63).Apps.Single().Status == CameraStatus.Active && state.Primary == primary, "Stale warm-up report caused false off.");
            state.Receive(candidate, [Activity("one", false, 64)], start.AddSeconds(64));
            Assert(Snapshot(state, 64).Apps.Single().Status == CameraStatus.Inactive && state.Primary == candidate, "Fresh stopped baseline could not recover a stale primary.");
        });
        Check("replacement failure and exact warm-up timeout retain a healthy primary with bounded retries", () =>
        {
            var (state, primary) = Ready();
            long candidate = state.Begin(start.AddSeconds(60));
            state.Fail(candidate, "fixture start failure", start.AddSeconds(61));
            Assert(Snapshot(state, 61).Healthy && state.Primary == primary && state.Epoch == 1, "Candidate failure poisoned primary.");
            Assert(!state.NeedsSource(start.AddSeconds(90.999)) && state.NeedsSource(start.AddSeconds(91)), "Retry deadline is wrong.");
            candidate = state.Begin(start.AddSeconds(91));
            state.Expire(start.AddSeconds(120.999));
            Assert(state.Contains(candidate), "Warm-up expired too early.");
            state.Expire(start.AddSeconds(121));
            Assert(!state.Contains(candidate) && Snapshot(state, 121).Healthy && state.Primary == primary, "Timed-out replacement discarded primary.");
        });
        Check("late warm-up activity cannot revive a capture after an explicit empty primary report", () =>
        {
            var (state, primary) = Ready();
            long candidate = state.Begin(start.AddSeconds(60));
            state.Receive(candidate, [Activity("one", true, 61)], start.AddSeconds(61));
            state.Receive(primary, [new("one", "Camera", [])], start.AddSeconds(62));
            Assert(Snapshot(state, 62).Apps.Single().Status == CameraStatus.Inactive && state.Primary == primary, "Old candidate start revived a stopped capture.");
            state.Receive(candidate, [new("one", "Camera", [])], start.AddSeconds(63));
            Assert(Snapshot(state, 63).Healthy && state.Primary == candidate, "Fresh empty baseline was not accepted.");
        });
        Check("partial replacements require every monitored baseline and fresh independent inputs", () =>
        {
            var (state, primary) = Ready();
            state.Receive(primary, [Activity("two", false, 0)], start);
            long candidate = state.Begin(start.AddSeconds(60));
            state.Receive(candidate, [Activity("one", false, 60)], start.AddSeconds(60));
            Assert(Snapshot(state, 60, []).Healthy && state.Primary == primary, "Partial candidate was promoted.");
            state.Receive(candidate, [Activity("two", false, 61)], start.AddSeconds(61));
            Assert(!Snapshot(state, 61, [], "Process catalog unavailable").Healthy && state.Primary == primary, "Stale identities permitted handover.");
            Assert(Snapshot(state, 62, []).Healthy && state.Primary == candidate, "Fresh full candidate was not promoted.");
        });
        Check("catalog outages retain subscriptions and callbacks but PID reuse remains unknown until fresh evidence", () =>
        {
            var (state, primary) = Ready();
            Assert(!Snapshot(state, 1, unavailable: "Process query failed").Healthy && state.Contains(primary), "Catalog error retired sensor.");
            state.Receive(primary, [Activity("one", true, 2)], start.AddSeconds(2));
            Assert(Snapshot(state, 3).Apps.Single().Status == CameraStatus.Active && state.Epoch == 1, "Catalog recovery lost a camera event.");
            var reused = process with { StartTime = start.AddSeconds(4).ToFileTime() };
            Assert(!Snapshot(state, 5, identity: reused).Healthy, "PID reuse trusted an old capture.");
            state.Receive(primary, [Activity("one", true, 6)], start.AddSeconds(6));
            Assert(Snapshot(state, 6, identity: reused).Healthy, "Fresh reused-PID report did not recover.");
        });
        Check("real primary loss and resume discard trust and reject all retired callbacks", () =>
        {
            var (state, primary) = Ready();
            state.Fail(primary, "native source failed", start.AddSeconds(1));
            Assert(!Snapshot(state, 1).Healthy && state.Epoch == 2, "Native failure retained known-idle.");
            Assert(!state.Receive(primary, [Activity("one", false, 2)], start.AddSeconds(2)), "Failed callback revived source.");
            long recovery = state.Begin(start.AddSeconds(3));
            Assert(!Snapshot(state, 3).Healthy, "Restart inherited previous baseline.");
            state.Receive(recovery, [Activity("one", false, 4)], start.AddSeconds(4));
            Assert(Snapshot(state, 4).Healthy, "Fresh recovery baseline was ignored.");
            long candidate = state.Begin(start.AddSeconds(64));
            state.Invalidate("System resumed", start.AddSeconds(65));
            Assert(!Snapshot(state, 65).Healthy && !state.Contains(recovery) && !state.Contains(candidate), "Resume reused pre-sleep state.");
        });
        Check("callback silence alone is not failure, but overdue independent verification cannot imply healthy forever", () =>
        {
            var (state, primary) = Ready();
            state.Expire(start.AddSeconds(90));
            Assert(Snapshot(state, 90).Healthy && state.Contains(primary), "Quiet primary was expired by callback age.");
            Assert(!Snapshot(state, 300).Healthy && state.Contains(primary), "Unverified observation retained trust indefinitely.");
            long candidate = state.Begin(start.AddSeconds(301));
            state.Receive(candidate, [Activity("one", false, 301)], start.AddSeconds(301));
            Assert(Snapshot(state, 301).Healthy && state.Primary == candidate, "Independent verification did not restore confidence.");
        });
        Check("a failed primary can use an independently ready candidate, and total loss retries promptly", () =>
        {
            var (state, primary) = Ready();
            long candidate = state.Begin(start.AddSeconds(60));
            state.Receive(candidate, [Activity("one", false, 60)], start.AddSeconds(60));
            state.Fail(primary, "primary failed", start.AddSeconds(61));
            Assert(Snapshot(state, 61).Healthy && state.Primary == candidate && state.Epoch == 2, "Ready independent source was discarded.");
            long replacement = state.Begin(start.AddSeconds(121));
            state.Fail(candidate, "primary failed again", start.AddSeconds(122));
            state.Fail(replacement, "candidate also failed", start.AddSeconds(123));
            Assert(!Snapshot(state, 123).Healthy && state.NeedsSource(start.AddSeconds(125)), "Total loss inherited the slower verification retry.");
        });
        Check("fresh selected-camera activity can still activate during incomplete global verification", () =>
        {
            var (state, primary) = Ready();
            state.Receive(primary, [Activity("one", true, 301)], start.AddSeconds(301));
            var snapshot = Snapshot(state, 301, []);
            Assert(!snapshot.Healthy && snapshot.Apps.Single().Status == CameraStatus.Active, "Incomplete second-camera baseline erased fresh positive evidence.");
        });
        return passed;
    }
}
