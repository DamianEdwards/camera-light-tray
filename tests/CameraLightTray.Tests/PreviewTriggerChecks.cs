using CameraLightTray.App.Diagnostics;
using CameraLightTray.App.Platform;
using CameraLightTray.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CameraLightTray.Tests;

internal static class PreviewTriggerChecks
{
    internal static int Run()
    {
        int passed = 0;
        var now = new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);
        CameraDevice[] cameras = [new("brio", "Example camera"), new("other-camera", "Other camera")];
        static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        void Test(string name, Action test) { test(); Console.WriteLine("PASS " + name); passed++; }
        ProcessIdentity Identity(int pid, string path) => new(pid, now.AddMinutes(-1).ToFileTime(), 1, "fixture", path, "CameraLightTray");
        Settings Settings() => new() { AutomationEnabled = true, CameraIds = ["brio"] };
        MonitorSnapshot Snapshot(SensorObservations source, ProcessIdentity[] processes, int ms) =>
            source.Snapshot(PreviewTrigger.Attribute(processes, 42), [], cameras, ["brio"], now.AddMilliseconds(ms));
        SensorActivity Report(bool streaming, int ms, int pid = 42, string camera = "brio") =>
            new(camera, "Example camera", [new(pid, streaming, now.AddMilliseconds(ms).ToFileTime())]);

        Test("built-in preview triggers from relocated and hosted executables without persisting a path selection", () =>
        {
            foreach (string path in new[] { @"C:\Portable\CameraLightTray.exe", @"D:\Worktree\bin\CameraLightTray.exe", @"C:\SDK\dotnet.exe" })
            {
                var saved = Settings();
                var effective = PreviewTrigger.Include(saved);
                var source = new SensorObservations(NullLogger.Instance);
                long generation = source.Begin(now);
                source.Receive(generation, [Report(true, 0)], now);
                ProcessIdentity[] processes = [Identity(42, path)];
                var decision = new AutomationDecision();
                Assert(!decision.Update(effective, Snapshot(source, processes, 0), ManualMode.Automatic, now), "Preview bypassed debounce.");
                var active = Snapshot(source, processes, 500);
                Assert(decision.Update(effective, active, ManualMode.Automatic, now.AddMilliseconds(500)), "Unselected built-in preview did not activate.");
                Assert(ActivityStatus.Evaluate(effective, active, ManualMode.Automatic, now.AddMilliseconds(500)).Title == "Camera in use", "Preview activation is missing from UI status.");
                Assert(saved.Apps.Count == 0 && processes.Single().AppKey == path &&
                    PreviewTrigger.Include(effective).Apps.Count == 1, "Runtime attribution changed saved settings or duplicated the implicit trigger.");
            }
        });
        Test("implicit preview attribution does not enable another process using the same executable path", () =>
        {
            var source = new SensorObservations(NullLogger.Instance);
            long generation = source.Begin(now);
            source.Receive(generation, [Report(true, 0, pid: 43)], now);
            ProcessIdentity[] processes = [Identity(42, @"C:\Portable\CameraLightTray.exe"), Identity(43, @"C:\Portable\CameraLightTray.exe")];
            var decision = new AutomationDecision();
            var effective = PreviewTrigger.Include(Settings());
            decision.Update(effective, Snapshot(source, processes, 0), ManualMode.Automatic, now);
            Assert(!decision.Update(effective, Snapshot(source, processes, 500), ManualMode.Automatic, now.AddMilliseconds(500)), "A separate instance was implicitly enabled by path.");
            Assert(Snapshot(source, processes, 500).Apps.Single(a => a.Key == PreviewTrigger.Key).Status == CameraStatus.Inactive,
                "Other process activity contaminated built-in preview status.");
        });
        Test("built-in preview stop retains leave grace and unavailable sensor state conservatively holds", () =>
        {
            var source = new SensorObservations(NullLogger.Instance);
            long generation = source.Begin(now);
            source.Receive(generation, [Report(true, 0)], now);
            ProcessIdentity[] processes = [Identity(42, "own-app")];
            var effective = PreviewTrigger.Include(Settings());
            var decision = new AutomationDecision();
            decision.Update(effective, Snapshot(source, processes, 0), ManualMode.Automatic, now);
            Assert(decision.Update(effective, Snapshot(source, processes, 500), ManualMode.Automatic, now.AddMilliseconds(500)), "Fixture did not activate.");
            source.Receive(generation, [Report(false, 1000)], now.AddSeconds(1));
            Assert(decision.Update(effective, Snapshot(source, processes, 1000), ManualMode.Automatic, now.AddSeconds(1)), "Stopping preview bypassed grace.");
            Assert(decision.Update(effective, Snapshot(source, processes, 3499), ManualMode.Automatic, now.AddMilliseconds(3499)), "Preview grace ended early.");
            Assert(!decision.Update(effective, Snapshot(source, processes, 3500), ManualMode.Automatic, now.AddMilliseconds(3500)), "Preview stop did not release.");
            decision.HoldRecoveredOwnership();
            source.Fail(generation, "source unavailable", now.AddSeconds(4));
            Assert(decision.Update(effective, Snapshot(source, processes, 10000), ManualMode.Automatic, now.AddSeconds(10)), "Source loss converted preview uncertainty to off.");
        });
        Test("preview remains gated by automation, pause, and monitored cameras, and never fabricates sensor activity", () =>
        {
            var saved = Settings();
            var source = new SensorObservations(NullLogger.Instance);
            long generation = source.Begin(now);
            ProcessIdentity[] processes = [Identity(42, "own-app")];
            var effective = PreviewTrigger.Include(saved);
            var decision = new AutomationDecision();
            Assert(!decision.Update(effective, Snapshot(source, processes, 1000), ManualMode.Automatic, now.AddSeconds(1)), "Missing baseline fabricated preview activity.");
            source.Receive(generation, [Report(true, 0, camera: "other-camera"), new("brio", "Example camera", [])], now);
            Assert(!decision.Update(effective, Snapshot(source, processes, 1000), ManualMode.Automatic, now.AddSeconds(1)), "Unmonitored preview camera triggered lighting.");
            source.Receive(generation, [Report(true, 1000)], now.AddSeconds(1));
            var active = Snapshot(source, processes, 2000);
            Assert(!decision.Update(effective, active, ManualMode.Paused, now.AddSeconds(2)), "Preview bypassed pause.");
            Assert(!decision.Update(effective with { AutomationEnabled = false }, active, ManualMode.Automatic, now.AddSeconds(2)), "Preview enabled automation on its own.");
            Assert(!decision.Update(effective, active with { Time = now }, ManualMode.Automatic, now.AddSeconds(5)), "Preview bypassed snapshot freshness.");
        });
        return passed;
    }
}
