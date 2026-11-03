namespace CameraLightTray.Core;

// State is scoped to an ETW epoch and process creation time, never to a bare PID.
public sealed class CameraTracker(long? baselineStart = null)
{
    private sealed class Entry(ProcessIdentity identity, bool baselineKnown)
    {
        public ProcessIdentity Identity { get; } = identity;
        public HashSet<(ulong Engine, bool Record)> Engines { get; } = [];
        public bool Uncertain { get; set; }
        public bool BaselineKnown { get; } = baselineKnown;
    }
    private readonly Dictionary<int, Entry> _processes = [];
    private readonly HashSet<int> _unresolvedOwners = [], _unattributedCaptures = [];
    private long _baselineStart = baselineStart ?? DateTimeOffset.UtcNow.ToFileTime();
    public long Epoch { get; private set; } = 1;
    public bool Healthy { get; private set; }
    public bool AttributionComplete => _unattributedCaptures.Count == 0;
    public string Detail { get; private set; } = "Monitor starting; existing captures are unknown.";

    public void Ready(long? observationStart = null)
    {
        _baselineStart = observationStart ?? DateTimeOffset.UtcNow.ToFileTime();
        _processes.Clear();
        _unresolvedOwners.Clear(); _unattributedCaptures.Clear();
        Healthy = true;
        Detail = "Capture-engine lifecycle monitoring. Restart apps that predate this monitor connection to establish a baseline; unsupported camera APIs remain unknown.";
    }
    public void Invalidate(string detail, bool sourceStillRunning = false)
    {
        Epoch++;
        Healthy = sourceStillRunning;
        Detail = detail;
        _baselineStart = DateTimeOffset.UtcNow.ToFileTime();
        _processes.Clear();
    }

    public void Synchronize(IEnumerable<ProcessIdentity> processes, IEnumerable<int>? unresolvedOwners = null)
    {
        _unresolvedOwners.Clear();
        if (unresolvedOwners is not null) _unresolvedOwners.UnionWith(unresolvedOwners);
        _unattributedCaptures.IntersectWith(_unresolvedOwners);
        var current = processes.ToDictionary(p => p.Pid);
        foreach (var pid in _processes.Keys.ToArray())
            if (!current.TryGetValue(pid, out var process) || process.StartTime != _processes[pid].Identity.StartTime)
                _processes.Remove(pid);
        foreach (var process in current.Values)
            if (!_processes.ContainsKey(process.Pid)) _processes.Add(process.Pid, new(process, process.StartTime >= _baselineStart));
    }

    public void UnattributedTransition(int pid, int eventId, uint hresult)
    {
        // Other sessions/users cannot poison this client's health. A capture from
        // a still-unresolved owner in this session must nevertheless block release.
        if (_unresolvedOwners.Contains(pid) && eventId is 207 or 209 or 211 or 213 && hresult == 0)
            _unattributedCaptures.Add(pid);
    }

    public void Transition(ProcessIdentity process, int eventId, ulong engine, uint hresult)
    {
        if (eventId is not (207 or 209 or 211 or 213) || hresult != 0) return;
        if (!_processes.TryGetValue(process.Pid, out var entry) || entry.Identity.StartTime != process.StartTime)
            _processes[process.Pid] = entry = new(process, process.StartTime >= _baselineStart);
        var key = (engine, eventId is 211 or 213);
        if (eventId is 207 or 211)
        {
            if (!entry.Engines.Add(key)) entry.Uncertain = true;
        }
        else
        {
            // An unmatched stop establishes only that engine's state, not the process baseline.
            if (!entry.Engines.Remove(key)) entry.Uncertain = true;
        }
    }

    public CameraApp[] Apps(string userSid, int sessionId) =>
        _processes.Values.Where(e => e.Identity.UserSid == userSid && e.Identity.SessionId == sessionId)
            .GroupBy(e => e.Identity.AppKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CameraApp(g.Key, g.First().Identity.Name,
                g.Any(e => e.Engines.Count > 0) ? CameraStatus.Active :
                Healthy && g.All(e => e.BaselineKnown && !e.Uncertain) ? CameraStatus.Inactive : CameraStatus.Unknown))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();
}
