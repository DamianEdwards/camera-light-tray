using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CameraLightTray.Core;
using Windows.Win32;

namespace CameraLightTray.Monitor;

internal sealed class MonitorHost : IDisposable
{
    private readonly object _gate = new();
    private readonly CameraTracker _tracker = new();
    private readonly EtwMonitor _etw;
    private HashSet<int> _uncertainSessions = [];
    private readonly List<Task> _clients = [];
    private Task? _poll, _accept;
    private long _sequence;
    internal MonitorHost() => _etw = new(Transition, Invalidate);

    internal void Start(CancellationToken token)
    {
        try { _etw.Start(() => { lock (_gate) _tracker.Ready(); }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Invalidate($"ETW unavailable ({ex.Message}). Use explicit setup/repair; no inactive state is inferred.");
        }
        _poll = PollAsync(token);
        _accept = AcceptAsync(token);
        Observe(_poll); Observe(_accept);
    }
    private void Observe(Task task) => _ = task.ContinueWith(t =>
        Invalidate("Monitor worker failed: " + t.Exception!.GetBaseException().Message),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    internal void Invalidate(string reason) { lock (_gate) _tracker.Invalidate(reason); }
    private void Transition(int pid, long timestamp, int eventId, ulong engine, uint hresult)
    {
        try
        {
            var process = ProcessCatalog.Read(pid);
            if (process is null) { Invalidate("A capture process identity could not be resolved."); return; }
            if (timestamp < process.StartTime) return; // A buffered event from a reused PID.
            lock (_gate) _tracker.Transition(process, eventId, engine, hresult);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Invalidate("Capture process disappeared before its identity could be verified."); }
    }

    private async Task PollAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!token.IsCancellationRequested)
        {
            var (processes, uncertain) = ProcessCatalog.Enumerate();
            lock (_gate)
            {
                _tracker.Synchronize(processes);
                _uncertainSessions = uncertain;
            }
            _etw.CheckLoss();
            if (!await timer.WaitForNextTickAsync(token)) break;
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null));
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create("CameraLightTray.Monitor.v1", PipeDirection.InOut, 16,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    }

    private async Task AcceptAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var pipe = CreatePipe();
            try { await pipe.WaitForConnectionAsync(token); }
            catch { pipe.Dispose(); throw; }
            var task = ServeAsync(pipe, token);
            _clients.RemoveAll(t => t.IsCompleted);
            _clients.Add(task); Observe(task);
            if (_clients.Count >= 12) await Task.WhenAny(_clients);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stopping)
    {
        using (pipe)
        using (var token = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        {
            token.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                byte[] handshake = new byte[1];
                if (await pipe.ReadAsync(handshake, token.Token) != 1 || handshake[0] != 1) return;
                string? sid = null;
                pipe.RunAsClient(() => { using var identity = WindowsIdentity.GetCurrent(); sid = identity.User?.Value; });
                if (sid is null || !PInvoke.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)) return;
                using var process = Process.GetProcessById((int)pid);
                int session = process.SessionId;
                if (session == 0) return;
                token.CancelAfter(Timeout.InfiniteTimeSpan);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                do
                {
                    MonitorSnapshot snapshot;
                    lock (_gate)
                    {
                        bool uncertain = _uncertainSessions.Contains(session);
                        snapshot = new(1, ++_sequence, _tracker.Epoch, DateTimeOffset.UtcNow,
                            _tracker.Healthy && !uncertain, uncertain ?
                            "Some process identities are inaccessible; state is unknown, not inactive." : _tracker.Detail,
                            _tracker.Apps(sid, session));
                    }
                    token.CancelAfter(TimeSpan.FromSeconds(5));
                    await writer.WriteLineAsync(JsonSerializer.Serialize(snapshot, AppJson.Default.MonitorSnapshot).AsMemory(), token.Token);
                    token.CancelAfter(Timeout.InfiniteTimeSpan);
                } while (await timer.WaitForNextTickAsync(token.Token));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ArgumentException)
            {
                // Disconnected/expired clients are isolated; they cannot change monitor configuration.
            }
        }
    }

    public void Dispose() => _etw.Dispose();
}
