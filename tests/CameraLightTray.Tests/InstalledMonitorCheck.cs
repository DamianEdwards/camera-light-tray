using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using CameraLightTray.App.Platform;

namespace CameraLightTray.Tests;

[SupportedOSPlatform("windows")]
internal static class InstalledMonitorCheck
{
    internal static async Task RunAsync(string resultPath)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Integration probe must run as a standard user, not an administrator.");
        using var process = Process.GetCurrentProcess();
        if (process.SessionId == 0) throw new InvalidOperationException("Integration probe requires a nonzero client session.");
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "CameraLightTray", "Monitor", "CameraLightTray.Monitor.exe");
        try
        {
            using var writable = File.Open(installed, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            throw new InvalidOperationException("Standard user can open installed helper for writing.");
        }
        catch (UnauthorizedAccessException) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string detail = "No monitor response.";
        int healthy = 0;
        long sequence = -1;
        var monitor = MonitorClient.RunAsync(snapshot =>
        {
            if (snapshot is null) return;
            detail = snapshot.Detail;
            if (!snapshot.Healthy || !snapshot.Apps.Any(a =>
                a.Key.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))) return;
            if (snapshot.Sequence <= sequence || DateTimeOffset.UtcNow - snapshot.Time > TimeSpan.FromSeconds(3))
                complete.TrySetException(new InvalidDataException("Stale/out-of-order helper snapshot."));
            sequence = snapshot.Sequence;
            if (++healthy >= 2) complete.TrySetResult();
        }, message => detail = message, timeout.Token);
        try { await complete.Task.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) { throw new InvalidOperationException("Healthy standard-user snapshot/current-process identity not established: " + detail); }
        finally { timeout.Cancel(); await monitor; }
        File.WriteAllText(resultPath,
            "PASS: actual setup installed LocalService helper; ETW StartTrace/EnableTraceEx2 healthy; " +
            "standard-user IPC authenticated LocalService, received two fresh healthy snapshots containing its own executable identity; " +
            "installed helper denied standard-user writes. No camera/light actions or provider ACL changes.");
    }
}
