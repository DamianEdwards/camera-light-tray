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
    private readonly EtwMonitor _etw;
    private sealed record CaptureEvent(long Sequence, int Pid, long Timestamp, int Id, ulong Engine, uint Result);
    private readonly Queue<CaptureEvent> _events = [];
    private long _eventSequence, _epoch = 1;
    private bool _healthy;
    private string _detail = "Camera monitor starting.";
    private readonly List<Task> _clients = [];
    private Task? _poll, _accept;
    private long _sequence;
    internal MonitorHost() => _etw = new(Transition, Invalidate);

    internal void Start(CancellationToken token)
    {
        try { _etw.Start(() => { lock (_gate) { _healthy = true; _detail = "Capture-engine lifecycle monitoring; pre-existing captures remain unknown."; } }); }
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

    internal void Invalidate(string reason)
    {
        lock (_gate) { _epoch++; _healthy = false; _detail = reason; _events.Clear(); }
    }
    private void Transition(int pid, long timestamp, int eventId, ulong engine, uint hresult)
    {
        lock (_gate)
        {
            _events.Enqueue(new(++_eventSequence, pid, timestamp, eventId, engine, hresult));
            if (_events.Count > 2048) _events.Dequeue();
        }
    }

    private async Task PollAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!token.IsCancellationRequested)
        {
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
                var tracker = new CameraTracker();
                tracker.Ready();
                long cursor, epoch;
                lock (_gate) { cursor = _events.TryPeek(out var oldest) ? oldest.Sequence - 1 : _eventSequence; epoch = _epoch; }
                token.CancelAfter(Timeout.InfiniteTimeSpan);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                do
                {
                    ProcessIdentity[] identities = [];
                    int[] unresolvedOwners = [];
                    bool uncertain = true;
                    string? identityError = null;
                    string identityDetail = "";
                    long enumerationStarted = DateTimeOffset.UtcNow.ToFileTime();
                    try { pipe.RunAsClient(() => (identities, uncertain, identityDetail, unresolvedOwners) = ProcessCatalog.Enumerate(sid, session)); }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                    { identityError = "Client identity enumeration failed: " + ex.Message; }
                    CaptureEvent[] events;
                    bool healthy, gap;
                    string detail;
                    long currentEpoch, sequence;
                    lock (_gate)
                    {
                        gap = _events.TryPeek(out var oldest) && cursor < oldest.Sequence - 1;
                        // Defer events newer than this catalog snapshot: a just-created
                        // client process may not have been enumerated yet.
                        events = _events.Where(e => e.Sequence > cursor).TakeWhile(e => e.Timestamp < enumerationStarted).ToArray();
                        currentEpoch = _epoch; healthy = _healthy; detail = _detail;
                        if (events.Length > 0) cursor = events[^1].Sequence;
                        sequence = ++_sequence;
                    }
                    if (epoch != currentEpoch || gap)
                    {
                        tracker.Invalidate(gap ? "Client capture-event buffer overflow; state unknown." : detail);
                        epoch = currentEpoch;
                    }
                    if (!identities.Any(p => p.Pid == pid))
                    {
                        uncertain = true;
                        identityDetail = "Authenticated client's own process identity is inaccessible; state unknown.";
                    }
                    if (uncertain) tracker.Invalidate(identityError ??
                        "Client process enumeration was incomplete; existing capture baselines are unknown.", healthy);
                    tracker.Synchronize(identities, unresolvedOwners);
                    var byPid = identities.ToDictionary(p => p.Pid);
                    foreach (var capture in events)
                        if (byPid.TryGetValue(capture.Pid, out var identity) && capture.Timestamp >= identity.StartTime)
                            tracker.Transition(identity, capture.Id, capture.Engine, capture.Result);
                        else tracker.UnattributedTransition(capture.Pid, capture.Id, capture.Result);
                    var snapshot = new MonitorSnapshot(1, sequence, (currentEpoch << 32) | tracker.Epoch, DateTimeOffset.UtcNow,
                        healthy && tracker.Healthy && tracker.AttributionComplete && !uncertain,
                        !healthy ? detail : identityError ?? (!tracker.AttributionComplete ?
                            "Capture lifecycle from an unresolved process owner in this session; camera state unknown until identity or lifetime is resolved." :
                            !tracker.Healthy ? tracker.Detail : uncertain ?
                            identityDetail : detail),
                        tracker.Apps(sid, session));
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
