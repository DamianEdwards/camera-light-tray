using CameraLightTray.App.Platform;
using CameraLightTray.Core;

namespace CameraLightTray.Tests;

internal static class SensorOverlapCheck
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static async Task RunAsync(string path)
    {
        var gate = new object();
        var first = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var second = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        void Receive(HashSet<string> target, SensorActivity[] reports)
        { lock (gate) foreach (var report in reports) target.Add(report.Id); }
        void Error(string error) { lock (gate) errors.Add(error); }
        async Task WaitAsync(Func<bool> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (true)
            {
                lock (gate)
                {
                    if (errors.Count != 0) throw new IOException(string.Join("; ", errors));
                    if (ready()) return;
                }
                await Task.Delay(100, timeout.Token);
            }
        }
        using var primary = new SensorActivityMonitor(reports => Receive(first, reports), Error);
        primary.Start();
        await WaitAsync(() => first.Count > 0);
        using var candidate = new SensorActivityMonitor(reports => Receive(second, reports), Error);
        candidate.Start();
        await WaitAsync(() => first.IsSubsetOf(second));
        primary.Dispose();
        await Task.Delay(1500);
        lock (gate)
        {
            if (errors.Count != 0) throw new IOException(string.Join("; ", errors));
            File.WriteAllText(path, $"PASS: overlapping native subscriptions acquired {first.Count} primary and {second.Count} candidate camera baselines; primary retirement succeeded while the candidate remained started. No camera capture or light writes.");
        }
    }
}
