using System.ComponentModel;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using CameraLightTray.Core;

namespace CameraLightTray.App.Platform;

[SupportedOSPlatform("windows")]
internal static class MonitorClient
{
    internal static NamedPipeClientStream Create(string name = "CameraLightTray.Monitor.v1") =>
        new(".", name, PipeAccessRights.ReadWrite, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation, HandleInheritability.None);
    internal static async Task<string?> ReadHeartbeatAsync(StreamReader reader, CancellationToken token)
    {
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(token);
        heartbeat.CancelAfter(TimeSpan.FromSeconds(4));
        try { return await reader.ReadLineAsync(heartbeat.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("The monitor connected but did not send a snapshot within 4 seconds. " +
                "The helper may be slow or unresponsive; this does not mean setup failed.");
        }
    }
    internal static async Task RunAsync(Action<MonitorSnapshot?> update, Action<string> status, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = Create();
                await pipe.ConnectAsync(2000, token);
                VerifyServer(pipe);
                await pipe.WriteAsync(new byte[] { 1 }, token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                long sequence = -1;
                while (true)
                {
                    string? line = await ReadHeartbeatAsync(reader, token);
                    if (line is null) throw new IOException("Monitor disconnected.");
                    if (line.Length > 1_000_000) throw new InvalidDataException("Monitor response exceeds limit.");
                    var snapshot = JsonSerializer.Deserialize(line, AppJson.Default.MonitorSnapshot)
                        ?? throw new InvalidDataException("Empty monitor snapshot.");
                    if (snapshot.Version != 1 || snapshot.Sequence <= sequence || snapshot.Apps is not { Length: <= 5000 } ||
                        snapshot.Apps.Any(a => a is null || string.IsNullOrWhiteSpace(a.Key) || string.IsNullOrWhiteSpace(a.Name)))
                        throw new InvalidDataException("Unsupported or out-of-order monitor snapshot.");
                    sequence = snapshot.Sequence;
                    update(snapshot); status(snapshot.Detail);
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or
                Win32Exception or UnauthorizedAccessException or JsonException)
            {
                update(null);
                if (token.IsCancellationRequested) return;
                status("Monitor unavailable: " + ex.Message +
                    (ex is TimeoutException ? " Reconnecting; camera state is unknown." :
                        " Check that the monitor service is installed and running. Camera state is unknown."));
            }
            try { await Task.Delay(2000, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }

    internal static void VerifyServer(NamedPipeClientStream pipe)
    {
        // A normal user cannot assign LocalService ownership to a spoofed pipe.
        // This does not depend on permission to open another account's process token.
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner?.Value != "S-1-5-19")
            throw new UnauthorizedAccessException("Monitor pipe owner is not LocalService.");
    }
}
