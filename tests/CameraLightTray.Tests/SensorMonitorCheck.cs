using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using CameraLightTray.App.Platform;
using CameraLightTray.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CameraLightTray.Tests;

internal static class SensorMonitorCheck
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static async Task RunAsync(string resultPath)
    {
        using var user = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        string sid = user.User!.Value;
        int session = process.SessionId, attempts = 0, failures = 0, healthy = 0;
        var state = new SensorObservations(NullLogger.Instance);
        var gate = new object();
        CameraDevice[] cameras = [];
        long primary = state.Begin(DateTimeOffset.UtcNow);
        using var monitor = new SensorActivityMonitor(reports =>
        {
            lock (gate)
            {
                state.Receive(primary, reports, DateTimeOffset.UtcNow);
                cameras = cameras.UnionBy(reports.Select(r => new CameraDevice(r.Id, "Camera")), c => c.Id,
                    StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }, error => { lock (gate) state.Fail(primary, error, DateTimeOffset.UtcNow); });
        monitor.Start();
        await SensorMonitorRecovery.RunAsync(async cancellation =>
        {
            if (++attempts == 1) throw new Win32Exception(1722, "Injected transient process-owner query failure.");
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                var catalog = CameraLightTray.Monitor.ProcessCatalog.Enumerate(sid, session);
                lock (gate)
                {
                    var excluded = catalog.OtherOwners.Concat(state.StreamingProcesses.Where(pid =>
                        CameraLightTray.Monitor.ProcessCatalog.IsOtherSession(pid, session))).ToArray();
                    var snapshot = state.Snapshot(catalog.Processes, excluded, cameras, [], DateTimeOffset.UtcNow);
                    bool current = snapshot.Healthy && state.Primary == primary &&
                        catalog.Processes.Any(p => p.Pid == process.Id && p.UserSid == sid);
                    healthy = current ? healthy + 1 : 0;
                }
                if (healthy >= 2) { stop.Cancel(); return; }
            } while (await timer.WaitForNextTickAsync(cancellation));
        }, ex =>
        {
            failures++;
            lock (gate)
            {
                if (state.Snapshot([], [], [], [], DateTimeOffset.UtcNow, ex.Message).Healthy ||
                    !state.Contains(primary) || state.Epoch != 1)
                    throw new InvalidOperationException("Catalog failure must be unknown without retiring the sensor subscription.");
            }
        }, TimeSpan.FromSeconds(2), stop.Token);
        if (healthy < 2 || failures != 1 || attempts != 2)
            throw new InvalidOperationException($"Sensor continuity failed: attempts={attempts}; failures={failures}; healthy={healthy}.");
        File.WriteAllText(resultPath, "PASS: injected process-owner failure produced unknown attribution but retained the same native sensor subscription; retry received two healthy normal-user snapshots without elevation, camera capture or light writes.");
    }
}
