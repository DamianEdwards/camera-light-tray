namespace CameraLightTray.Core;

public sealed record CameraDevice(string Id, string Name);
public sealed record SensorProcess(int Pid, bool Streaming, long ReportTime);
public sealed record SensorActivity(string Id, string Name, SensorProcess[] Processes);

public sealed class SensorState
{
    private SensorActivity[] _reports = [];
    private bool _ready;
    private string _error = "Waiting for Windows camera activity reports.";
    private long _epoch = 1, _sequence;

    public void Receive(SensorActivity[] reports)
    {
        _reports = reports;
        _ready = true;
        _error = "";
    }

    public void Invalidate(string error)
    {
        _epoch++;
        _ready = false;
        _reports = [];
        _error = error;
    }

    public int[] StreamingProcesses => _reports.SelectMany(r => r.Processes).Where(p => p.Streaming)
        .Select(p => p.Pid).Distinct().ToArray();

    public MonitorSnapshot Snapshot(ProcessIdentity[] processes, int[] excludedProcesses,
        CameraDevice[] cameras, IReadOnlyCollection<string> selected, DateTimeOffset now)
    {
        bool missing = selected.Any(id => !cameras.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
        var reports = _reports.Where(r => selected.Count == 0 || selected.Contains(r.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
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
        bool healthy = _ready && !missing && !unattributed;
        string detail = !_ready ? _error : missing ? "A selected camera is unavailable; camera state is unknown." :
            unattributed ? "A streaming camera client cannot be attributed safely; camera state is unknown." :
            "Windows sensor activity reports connected. " + (selected.Count == 0 ? "Monitoring all cameras." : $"Monitoring {selected.Count} selected camera(s).");
        var apps = processes.GroupBy(p => p.AppKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CameraApp(g.Key, g.First().Name, g.Any(p => active.Contains(p.Pid)) ?
                CameraStatus.Active : healthy ? CameraStatus.Inactive : CameraStatus.Unknown)).ToArray();
        return new(1, ++_sequence, _epoch, now, healthy, detail, apps);
    }
}
