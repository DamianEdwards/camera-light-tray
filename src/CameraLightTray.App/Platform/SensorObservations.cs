using CameraLightTray.Core;
using Microsoft.Extensions.Logging;

namespace CameraLightTray.App.Platform;

internal sealed class SensorObservations(ILogger log)
{
    internal static readonly TimeSpan VerificationInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan VerificationTrust = TimeSpan.FromMinutes(5);
    private readonly Dictionary<long, Observation> _sources = [];
    private long _generation, _sequence;
    private long? _primary, _candidate;
    private DateTimeOffset _nextAttempt;
    private string _error = "Waiting for Windows camera activity reports.", _warning = "";
    internal long Epoch { get; private set; } = 1;
    internal long? Primary => _primary;
    internal long? Candidate => _candidate;
    internal DateTimeOffset LastCallback => _primary is long id ? _sources[id].LastCallback : default;
    internal int[] StreamingProcesses => _sources.Values.SelectMany(s => s.State.StreamingProcesses).Distinct().ToArray();
    private sealed class Observation(DateTimeOffset started)
    {
        internal readonly DateTimeOffset Started = started;
        internal readonly SensorState State = new();
        internal readonly Dictionary<string, (SensorActivity Report, DateTimeOffset Received)> Updates = new(StringComparer.OrdinalIgnoreCase);
        internal DateTimeOffset LastCallback;
        internal DateTimeOffset Verified;
    }

    internal bool Contains(long generation) => _sources.ContainsKey(generation);
    internal bool NeedsSource(DateTimeOffset now) => _candidate is null && now >= _nextAttempt;
    internal long Begin(DateTimeOffset now)
    {
        if (_candidate is not null) throw new InvalidOperationException("A replacement is already warming.");
        long id = ++_generation;
        _sources.Add(id, new(now));
        if (_primary is null) { _primary = id; _warning = ""; }
        else _candidate = id;
        _nextAttempt = now + VerificationInterval;
        log.LogInformation("Sensor subscription starting; generation={Generation}, primary={Primary}, candidate={Candidate}, epoch={Epoch}",
            id, _primary, _candidate, Epoch);
        return id;
    }
    internal bool Receive(long generation, SensorActivity[] reports, DateTimeOffset now)
    {
        if (!_sources.TryGetValue(generation, out var source)) return false;
        source.State.Receive(reports);
        source.LastCallback = now;
        foreach (var report in reports) source.Updates[report.Id] = (report, now);
        return true;
    }
    internal void Fail(long generation, string error, DateTimeOffset now)
    {
        if (!_sources.Remove(generation)) return;
        bool primary = _primary == generation;
        if (primary)
        {
            _primary = null;
            Epoch++;
            _error = "Windows sensor subscription failed: " + error;
        }
        else { _candidate = null; _warning = "Background sensor verification delayed: " + error; }
        _nextAttempt = now.AddSeconds(_primary is null ? 2 : 30);
        log.LogWarning("Sensor subscription failed; generation={Generation}, wasPrimary={WasPrimary}, remainingPrimary={Primary}, candidate={Candidate}, epoch={Epoch}, error={Error}",
            generation, primary, _primary, _candidate, Epoch, error);
    }
    internal void Expire(DateTimeOffset now)
    {
        foreach (var pair in _sources.ToArray())
            if (now - pair.Value.Started >= WarmupTimeout &&
                (pair.Key == _candidate || !pair.Value.State.Ready))
                Fail(pair.Key, "Fresh complete camera baselines were not available within 30 seconds.", now);
    }
    internal void Invalidate(string error, DateTimeOffset now)
    {
        _sources.Clear();
        _primary = _candidate = null;
        _error = error; _warning = "";
        _nextAttempt = now;
        Epoch++;
        log.LogInformation("Sensor observations invalidated; epoch={Epoch}, reason={Reason}", Epoch, error);
    }
    internal MonitorSnapshot Snapshot(ProcessIdentity[] processes, int[] excluded, CameraDevice[] cameras,
        IReadOnlyCollection<string> selected, DateTimeOffset now, string? unavailable = null)
    {
        if (unavailable is not null) return Unknown(processes, now, unavailable);
        if (_candidate is long candidate)
        {
            var replacement = _sources[candidate];
            var snapshot = replacement.State.Snapshot(processes, excluded, cameras, selected, now);
            if (snapshot.Healthy && CanReplace(replacement, processes, selected, now))
            {
                long? previous = _primary;
                if (previous is long old) _sources.Remove(old);
                _primary = candidate; _candidate = null;
                _warning = "";
                replacement.Verified = now;
                _nextAttempt = now + VerificationInterval;
                // Verification preserves observation continuity and debounce/grace timers.
                log.LogInformation("Warm sensor handover completed; retired={Retired}, primary={Primary}, epoch={Epoch}, warmupMs={WarmupMs}",
                    previous, candidate, Epoch, (now - replacement.Started).TotalMilliseconds);
            }
        }
        if (_primary is not long primary) return Unknown(processes, now, _error);
        var source = _sources[primary];
        var current = source.State.Snapshot(processes, excluded, cameras, selected, now);
        var monitored = cameras.Where(c => selected.Count == 0 || selected.Contains(c.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (current.Healthy && (source.Verified == default || monitored.Length > 0 &&
            monitored.All(c => source.Updates.TryGetValue(c.Id, out var update) && update.Received > source.Verified)))
            source.Verified = now;
        if (source.Verified != default && now - source.Verified >= VerificationTrust)
        {
            var freshActive = source.Updates.Where(p => now - p.Value.Received < VerificationTrust &&
                monitored.Any(c => c.Id.Equals(p.Key, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(p => p.Value.Report.Processes).Where(p => p.Streaming).Select(p => p.Pid).ToHashSet();
            var uncertain = Unknown(processes, now, "Fresh complete camera baselines could not be verified for five minutes; camera state is unknown.");
            return uncertain with
            {
                Apps = uncertain.Apps.Select(app => current.Apps.Any(a => a.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase) && a.Status == CameraStatus.Active) &&
                    processes.Any(p => p.AppKey.Equals(app.Key, StringComparison.OrdinalIgnoreCase) && freshActive.Contains(p.Pid)) ?
                    app with { Status = CameraStatus.Active } : app).ToArray()
            };
        }
        return current with
        {
            Sequence = ++_sequence, Epoch = Epoch,
            Detail = current.Detail + (_warning.Length == 0 ? "" : " " + _warning + " Primary subscription remains active.")
        };
    }

    private bool CanReplace(Observation replacement, ProcessIdentity[] processes, IReadOnlyCollection<string> selected, DateTimeOffset now)
    {
        if (_primary is not long primary) return true;
        foreach (var pair in _sources[primary].Updates)
        {
            if (selected.Count > 0 && !selected.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)) continue;
            if (!replacement.Updates.TryGetValue(pair.Key, out var next)) continue;
            foreach (var active in next.Report.Processes.Where(p => p.Streaming))
                if (!pair.Value.Report.Processes.Any(p => p.Pid == active.Pid) &&
                    active.ReportTime < pair.Value.Received.ToFileTime()) return false;
            foreach (var previous in pair.Value.Report.Processes)
            {
                var identity = processes.FirstOrDefault(p => p.Pid == previous.Pid);
                if (identity is null || previous.ReportTime < identity.StartTime ||
                    previous.ReportTime > now.AddSeconds(1).ToFileTime()) continue;
                var current = next.Report.Processes.FirstOrDefault(p => p.Pid == previous.Pid);
                // Do not replace a more recent primary transition with an older warm-up report.
                if (current is not null && (current.ReportTime < previous.ReportTime ||
                    current.ReportTime == previous.ReportTime && current.Streaming != previous.Streaming &&
                    next.Received < pair.Value.Received)) return false;
                if (current is null && next.Received < pair.Value.Received) return false;
            }
        }
        return true;
    }
    private MonitorSnapshot Unknown(ProcessIdentity[] processes, DateTimeOffset now, string detail) =>
        new(1, ++_sequence, Epoch, now, false, detail,
            processes.GroupBy(p => p.AppKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CameraApp(g.Key, g.First().Name, CameraStatus.Unknown)).ToArray());
}
