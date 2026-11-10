using System.Collections.Concurrent;
using System.Text;
using CameraLightTray.App.Diagnostics;
using CameraLightTray.Core;
using Microsoft.Extensions.Logging;

namespace CameraLightTray.Tests;

internal static class LoggingChecks
{
    internal static async Task<int> RunAsync()
    {
        int passed = 0;
        void Check(string name, Action<string> test)
        {
            string directory = Path.Combine(Path.GetTempPath(), "CameraLightTray-log-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { test(directory); Console.WriteLine("PASS " + name); passed++; }
            finally { Directory.Delete(directory, true); }
        }
        static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        static string Read(string directory) => string.Join("", Directory.GetFiles(directory, "*.log").Order().Select(File.ReadAllText));
        static void Failure(string? error) { if (error is not null) throw new IOException(error); }
        static ILoggerFactory Factory(string directory, Action<string?> failure, FileLogOptions? options, TimeProvider clock)
        {
            var factory = new LoggerFactory();
            factory.AddProvider(new FileLoggerProvider(directory, failure, options, clock));
            return factory;
        }
        var clock = new Clock(new(2026, 11, 9, 23, 59, 59, TimeSpan.Zero));

        Check("ILogger files preserve UTC timestamps, scopes, fields and exceptions without line injection", directory =>
        {
            using (var factory = Factory(directory, Failure, null, clock))
            {
                var logger = factory.CreateLogger("Feature.Camera");
                using var scope = logger.BeginScope("observation-7");
                logger.LogInformation(new EventId(42, "Transition"), new IOException("fixture exception"), "State {Reason}\nforged", "snapshot-stale");
                logger.LogDebug("hidden-debug");
            }
            string text = Read(directory);
            Assert(text.Contains("2026-11-09T23:59:59") && text.Contains("scope=observation-7") &&
                text.Contains("Reason=snapshot-stale") && text.Contains("fixture exception") && text.Contains("event=42:Transition"), "Missing log context.");
            Assert(File.ReadAllLines(Directory.GetFiles(directory).Single()).Length == 1 && text.Contains("\\nforged") &&
                !text.Contains("hidden-debug"), "Level filtering or newline escaping failed.");
        });
        Check("daily rotation uses UTC and restarts append rather than overwrite", directory =>
        {
            void Write(string message)
            {
                using var factory = Factory(directory, Failure, null, clock);
                factory.CreateLogger("Test").LogInformation("{Message}", message);
            }
            Write("before-midnight");
            Write("same-day-restart");
            Assert(Directory.GetFiles(directory).Length == 1, "Restart should append the current file.");
            clock.Now = clock.Now.AddSeconds(2);
            Write("after-midnight");
            Assert(Directory.GetFiles(directory).Length == 2 && Read(directory).Contains("before-midnight") &&
                Read(directory).Contains("same-day-restart") && Read(directory).Contains("after-midnight"), "Daily rollover lost entries.");
            using (var factory = Factory(directory, Failure, null, clock))
            {
                var logger = factory.CreateLogger("Live rollover");
                logger.LogInformation("before live rollover");
                clock.Now = clock.Now.AddDays(1);
                logger.LogInformation("after live rollover");
            }
            Assert(Directory.GetFiles(directory).Length == 3 && Read(directory).Contains("after live rollover"), "Live daily rotation failed.");
        });
        Check("size rotation and file count enforce strict disk bounds", directory =>
        {
            using (var factory = Factory(directory, Failure, new() { MaxFileBytes = 512, MaxFiles = 4 }, clock))
                for (int i = 0; i < 30; i++) factory.CreateLogger("Size").LogInformation("Entry {Index} {Padding}", i, new string('x', 240));
            var files = Directory.GetFiles(directory);
            Assert(files.Length == 4 && files.All(path => new FileInfo(path).Length <= 512), "File rotation exceeded configured bounds.");
            Assert(Read(directory).Contains("Entry 29"), "Newest entry was not retained.");
        });
        Check("oversized Unicode records truncate explicitly and remain valid UTF-8 within the limit", directory =>
        {
            using (var factory = Factory(directory, Failure, new() { MaxFileBytes = 512 }, clock))
                factory.CreateLogger("Unicode").LogInformation("{Text}", string.Concat(Enumerable.Repeat("\U0001F4A1", 500)));
            byte[] data = File.ReadAllBytes(Directory.GetFiles(directory).Single());
            string text = new UTF8Encoding(false, true).GetString(data);
            Assert(data.Length <= 512 && text.EndsWith(" [truncated]\n"), "Oversized record was not safely bounded.");
        });
        Check("retention removes old owned logs but leaves unrelated files untouched", directory =>
        {
            File.WriteAllText(Path.Combine(directory, "camera-light-tray-2026-01-01-0001.log"), "old");
            File.WriteAllText(Path.Combine(directory, "other.log"), "retain");
            File.WriteAllText(Path.Combine(directory, "camera-light-tray-not-ours.log"), "retain");
            using (var factory = Factory(directory, Failure, null, clock))
                factory.CreateLogger("Retention").LogInformation("current");
            Assert(!File.Exists(Path.Combine(directory, "camera-light-tray-2026-01-01-0001.log")) &&
                File.ReadAllText(Path.Combine(directory, "other.log")) == "retain" &&
                File.Exists(Path.Combine(directory, "camera-light-tray-not-ours.log")), "Retention touched unrelated files or retained expired logs.");
        });
        Check("concurrent logging drains completely on disposal", directory =>
        {
            using (var factory = Factory(directory, Failure, null, clock))
            {
                var logger = factory.CreateLogger("Concurrent");
                Parallel.For(0, 200, i => logger.LogInformation("Concurrent entry {Index}", i));
            }
            Assert(Directory.GetFiles(directory).Sum(path => File.ReadAllLines(path).Length) == 200, "Shutdown lost concurrent entries.");
        });
        Check("light logs suppress unchanged polls but retain failures, recovery and writes", directory =>
        {
            bool offline = false;
            var device = new LightDevice("fixture:private-hardware-id", "Example light", "192.0.2.1", 9123, "fixture");
            using (var factory = Factory(directory, Failure, null, clock))
            {
                var transport = new LoggingLightTransport(new DelegateLights((_, _) => offline ?
                    Task.FromException<LightState>(new IOException("offline fixture")) : Task.FromResult(new LightState(0, 40, 220)),
                    (_, _, _) => Task.CompletedTask), factory.CreateLogger("Light"));
                transport.ReadAsync(device, default).GetAwaiter().GetResult();
                transport.ReadAsync(device, default).GetAwaiter().GetResult();
                offline = true;
                for (int i = 0; i < 2; i++)
                {
                    try { transport.ReadAsync(device, default).GetAwaiter().GetResult(); throw new InvalidOperationException("Expected read failure."); }
                    catch (IOException) { }
                }
                offline = false;
                transport.ReadAsync(device, default).GetAwaiter().GetResult();
                transport.WriteAsync(device, new(1, 40, 220), default).GetAwaiter().GetResult();
            }
            string text = Read(directory);
            Assert(text.Split('\n').Count(line => line.Contains("Light readback")) == 3 &&
                text.Contains("recovered=True") && text.Contains("Light write acknowledged") &&
                text.Contains("light=" + LogIdentity.Token(device.Id)) && !text.Contains(device.Id), "Light logging omitted context or logged every poll.");
        });
        string failureDirectory = Path.Combine(Path.GetTempPath(), "CameraLightTray-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(failureDirectory);
        try
        {
            string path = Path.Combine(failureDirectory, $"camera-light-tray-{clock.Now:yyyy-MM-dd}-0001.log");
            using var blocker = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var failures = new ConcurrentQueue<string?>();
            using (var factory = Factory(failureDirectory, error =>
            {
                failures.Enqueue(error);
                if (error?.Contains("write failed") == true)
                {
                    reported.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Logging fixture was not released.");
                }
            }, new() { QueueCapacity = 1 }, clock))
            {
                var logger = factory.CreateLogger("Failure");
                logger.LogInformation("blocked write");
                try
                {
                    await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    logger.LogInformation("recovered entry");
                    logger.LogInformation("overflow entry");
                }
                finally { blocker.Dispose(); release.Set(); }
            }
            string text = Read(failureDirectory);
            Assert(failures.Any(f => f?.Contains("queue is full") == true) && failures.Any(f => f is null) &&
                text.Contains("droppedEntries=1") && text.Contains("lostEntries=1") && text.Contains("recovered entry"),
                "I/O failure, queue loss or recovery was silent.");
            Console.WriteLine("PASS file failures and queue overflow are surfaced, counted and recover on subsequent writes");
            passed++;
        }
        finally { Directory.Delete(failureDirectory, true); }

        Check("activity reason codes distinguish health changes from the exact four-second freshness boundary", _ =>
        {
            var settings = new Settings { AutomationEnabled = true, Apps = [new("example", "Example", true)] };
            var snapshot = new MonitorSnapshot(1, 1, 2, clock.Now, true, "connected", [new("example", "Example", CameraStatus.Inactive)]);
            Assert(ActivityStatus.Evaluate(settings, snapshot, ManualMode.Automatic, clock.Now.AddMilliseconds(3999)).Code == "healthy-idle", "Fresh idle status changed.");
            Assert(ActivityStatus.Evaluate(settings, snapshot, ManualMode.Automatic, clock.Now.AddSeconds(4)).Code == "snapshot-stale", "Stale boundary not logged accurately.");
            Assert(ActivityStatus.Evaluate(settings, null, ManualMode.Automatic, clock.Now).Code == "snapshot-unavailable", "Missing snapshot reason lost.");
            Assert(ActivityStatus.Evaluate(settings, snapshot with { Healthy = false }, ManualMode.Automatic, clock.Now).Code == "sensor-unhealthy", "Health reason lost.");
            Assert(ActivityStatus.Evaluate(settings, snapshot with { Healthy = false, Apps = [new("example", "Example", CameraStatus.Active)] },
                ManualMode.Automatic, clock.Now).Title == "Camera in use", "Logging changed positive-activity behavior.");
        });
        return passed;
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
