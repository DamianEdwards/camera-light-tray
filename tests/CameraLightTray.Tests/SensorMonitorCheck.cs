using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using CameraLightTray.App.Platform;
using CameraLightTray.Core;

namespace CameraLightTray.Tests;

internal static class SensorMonitorCheck
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static async Task RunAsync(string resultPath)
    {
        using var user = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        string sid = user.User!.Value;
        int session = process.SessionId, attempts = 0, failures = 0, healthy = 0;
        var state = new SensorState();
        var gate = new object();
        await SensorMonitorRecovery.RunAsync(async cancellation =>
        {
            long epoch;
            CameraDevice[] cameras = [];
            string? sourceError = null;
            lock (gate) { state.Invalidate("Starting a fresh observation."); epoch = state.Epoch; }
            using var monitor = new SensorActivityMonitor(reports =>
            {
                lock (gate)
                {
                    if (!state.Receive(epoch, reports)) return;
                    cameras = cameras.UnionBy(reports.Select(r => new CameraDevice(r.Id, "Camera")), c => c.Id,
                        StringComparer.OrdinalIgnoreCase).ToArray();
                }
            }, error => { lock (gate) sourceError = error; });
            monitor.Start();
            if (++attempts == 1) throw new Win32Exception(1722, "Injected transient process-owner query failure.");
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                var catalog = CameraLightTray.Monitor.ProcessCatalog.Enumerate(sid, session);
                lock (gate)
                {
                    if (sourceError is not null) throw new IOException(sourceError);
                    var excluded = catalog.OtherOwners.Concat(state.StreamingProcesses.Where(pid =>
                        CameraLightTray.Monitor.ProcessCatalog.IsOtherSession(pid, session))).ToArray();
                    var snapshot = state.Snapshot(catalog.Processes, excluded, cameras, [], DateTimeOffset.UtcNow);
                    bool current = state.Ready && snapshot.Healthy &&
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
                state.Invalidate(ex.Message);
                if (state.Snapshot([], [], [], [], DateTimeOffset.UtcNow).Healthy)
                    throw new InvalidOperationException("Failed observation must be unknown.");
            }
        }, TimeSpan.FromSeconds(2), stop.Token);
        if (healthy < 2 || failures != 1 || attempts != 2)
            throw new InvalidOperationException($"Sensor recovery failed: attempts={attempts}; failures={failures}; healthy={healthy}.");
        File.WriteAllText(resultPath, "PASS: injected process-owner failure disposed the native source; automatic retry " +
            "received two fresh healthy normal-user sensor/identity snapshots without elevation, camera capture or light writes.");
    }
}
