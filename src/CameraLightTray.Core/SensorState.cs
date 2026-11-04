namespace CameraLightTray.Core;

public sealed record CameraDevice(string Id, string Name);
public sealed record SensorProcess(int Pid, bool Streaming, long ReportTime);
public sealed record SensorActivity(string Id, string Name, SensorProcess[] Processes);

public sealed class SensorState
{
    private readonly Dictionary<string, SensorActivity> _reports = new(StringComparer.OrdinalIgnoreCase);
    private bool _ready;
    private string _error = "Waiting for Windows camera activity reports.";
    private long _epoch = 1, _sequence;

    public long Epoch => _epoch;
    public bool Ready => _ready;

    public bool Receive(long epoch, SensorActivity[] reports)
    {
        if (epoch != _epoch) return false;
        Receive(reports);
        return true;
    }

    public void Receive(SensorActivity[] reports)
    {
        if (reports.Length == 0) return;
        foreach (var report in reports) _reports[report.Id] = report;
        _ready = true;
        _error = "";
    }

    public void Invalidate(string error)
    {
        _epoch++;
        _ready = false;
        _reports.Clear();
        _error = error;
    }

    public int[] StreamingProcesses => _reports.Values.SelectMany(r => r.Processes).Where(p => p.Streaming)
        .Select(p => p.Pid).Distinct().ToArray();

    public MonitorSnapshot Snapshot(ProcessIdentity[] processes, int[] excludedProcesses,
        CameraDevice[] cameras, IReadOnlyCollection<string> selected, DateTimeOffset now)
    {
        bool missing = selected.Any(id => !cameras.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
        bool unreported = cameras.Any(c => (selected.Count == 0 || selected.Contains(c.Id, StringComparer.OrdinalIgnoreCase)) &&
            !_reports.ContainsKey(c.Id));
        var reports = _reports.Values.Where(r => cameras.Any(c => c.Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase)) &&
            (selected.Count == 0 || selected.Contains(r.Id, StringComparer.OrdinalIgnoreCase))).ToArray();
        var byPid = processes.ToDictionary(p => p.Pid);
        var active = new HashSet<int>();
        bool unattributed = false;
        foreach (var client in reports.SelectMany(r => r.Processes).Where(p => p.Streaming))
        {
            if (byPid.TryGetValue(client.Pid, out var identity))
            {
                if (client.ReportTime >= identity.StartTime && client.ReportTime <= now.AddSeconds(1).ToFileTime())
                    active.Add(client.Pid);
                else unattributed = true;
            }
            else if (!excludedProcesses.Contains(client.Pid)) unattributed = true;
        }
        bool healthy = _ready && !missing && !unreported && !unattributed;
        string detail = !_ready ? _error : missing ? "A selected camera is unavailable; camera state is unknown." :
            unreported ? "Waiting for activity reports for every monitored camera; camera state is unknown." :
            unattributed ? "A streaming camera client cannot be attributed safely; camera state is unknown." :
            "Windows sensor activity reports connected. " + (selected.Count == 0 ? "Monitoring all cameras." : $"Monitoring {selected.Count} selected camera(s).");
        var apps = processes.GroupBy(p => p.AppKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CameraApp(g.Key, g.First().Name, g.Any(p => active.Contains(p.Pid)) ?
                CameraStatus.Active : healthy ? CameraStatus.Inactive : CameraStatus.Unknown)).ToArray();
        return new(1, ++_sequence, _epoch, now, healthy, detail, apps);
    }
}
