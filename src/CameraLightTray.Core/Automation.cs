namespace CameraLightTray.Core;

public interface ILightTransport
{
    Task<LightState> ReadAsync(LightDevice device, CancellationToken cancellationToken);
    Task WriteAsync(LightDevice device, LightState state, CancellationToken cancellationToken);
}

public sealed class AutomationDecision
{
    private DateTimeOffset? _activeSince, _inactiveSince;
    private long? _epoch;
    public bool Desired { get; private set; }
    public void HoldRecoveredOwnership() => Desired = true;

    public bool Update(Settings settings, MonitorSnapshot? snapshot, ManualMode mode, DateTimeOffset now)
    {
        if (!settings.AutomationEnabled || mode == ManualMode.Paused)
        {
            _activeSince = _inactiveSince = null;
            return Desired = false; // Release only states we still own.
        }
        if (mode is ManualMode.ForceOn or ManualMode.ForceOff)
        {
            _activeSince = _inactiveSince = null;
            return Desired = mode == ManualMode.ForceOn;
        }
        if (snapshot is null || now - snapshot.Time > TimeSpan.FromSeconds(4) ||
            snapshot.Time > now + TimeSpan.FromSeconds(1))
        {
            _activeSince = _inactiveSince = null;
            return Desired;
        }
        if (_epoch != snapshot.Epoch)
        {
            _epoch = snapshot.Epoch;
            _activeSince = _inactiveSince = null;
        }
        var selected = settings.Apps.Where(a => a.Enabled).ToArray();
        var states = selected.Select(a => snapshot.Apps.FirstOrDefault(p =>
            p.Key.Equals(a.Key, StringComparison.OrdinalIgnoreCase))?.Status ?? CameraStatus.Inactive).ToArray();
        bool active = states.Any(s => s == CameraStatus.Active);
        bool unknown = states.Any(s => s == CameraStatus.Unknown);
        if (active)
        {
            _inactiveSince = null;
            _activeSince ??= now;
            if (now - _activeSince >= TimeSpan.FromMilliseconds(settings.ActivationMilliseconds)) Desired = true;
        }
        else
        {
            _activeSince = null;
            if (unknown || !snapshot.Healthy) _inactiveSince = null;
            else
            {
                _inactiveSince ??= now;
                if (now - _inactiveSince >= TimeSpan.FromMilliseconds(settings.LeaveMilliseconds)) Desired = false;
            }
        }
        return Desired;
    }
}

public sealed class LightAutomation(ILightTransport transport, Action<OwnershipJournal> persist)
{
    private readonly Dictionary<string, OwnedLight> _owned = [];
    private readonly HashSet<string> _suppressed = [];
    public IReadOnlyCollection<OwnedLight> Owned => _owned.Values;
    public string Status { get; private set; } = "Automation is off.";
    public void Recover(OwnershipJournal journal)
    {
        foreach (var item in journal.Lights) _owned.TryAdd(item.Device.Id, item);
    }
    private void Save() => persist(new(_owned.Values.ToList()));

    public async Task ReconcileAsync(bool desired, IEnumerable<LightDevice> devices, CancellationToken token)
    {
        var failures = new List<Exception>();
        var enabled = devices.Where(d => d.Enabled && !d.Tls).ToDictionary(d => d.Id);
        foreach (var item in _owned.Values.ToArray())
        {
            try
            {
                var current = await transport.ReadAsync(item.Device, token);
                if (!item.AppliedConfirmed && current == item.Before)
                {
                    if (desired && enabled.ContainsKey(item.Device.Id))
                    {
                        await transport.WriteAsync(item.Device, item.Applied, token);
                        _owned[item.Device.Id] = item with { AppliedConfirmed = true };
                        Save();
                        Status = "Camera activity: lights on (owned changes only).";
                    }
                    else
                    {
                        _owned.Remove(item.Device.Id);
                        Save();
                        Status = "Previous light state unchanged; pending activation released.";
                    }
                }
                else if (current != item.Applied)
                {
                    _owned.Remove(item.Device.Id);
                    _suppressed.Add(item.Device.Id);
                    Save();
                    Status = "External light change detected; leaving it alone until this activation ends.";
                }
                else if (!desired || !enabled.ContainsKey(item.Device.Id))
                {
                    await transport.WriteAsync(item.Device, item.Before, token);
                    _owned.Remove(item.Device.Id);
                    Save();
                    Status = "Previous light state restored.";
                }
            }
            catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or
                OperationCanceledException && !token.IsCancellationRequested)
            {
                failures.Add(new IOException($"{item.Device.Name}: {ex.Message}", ex));
            }
        }
        if (!desired) _suppressed.Clear();
        else foreach (var device in enabled.Values)
        {
            if (_owned.ContainsKey(device.Id) || _suppressed.Contains(device.Id)) continue;
            try
            {
                var before = await transport.ReadAsync(device, token);
                var applied = before with { On = 1 };
                if (before == applied) continue;
                // Persist intent before the write, so a crash cannot lose the restoration record.
                _owned.Add(device.Id, new(device, before, applied, AppliedConfirmed: false));
                Save();
                await transport.WriteAsync(device, applied, token);
                _owned[device.Id] = _owned[device.Id] with { AppliedConfirmed = true };
                Save();
                Status = "Camera activity: lights on (owned changes only).";
            }
            catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or
                OperationCanceledException && !token.IsCancellationRequested)
            {
                failures.Add(new IOException($"{device.Name}: {ex.Message}", ex));
            }
        }
        if (failures.Count > 0) throw new IOException(
            $"{failures.Count} light operation(s) failed. {string.Join(" ", failures.Select(e => e.Message))}",
            new AggregateException(failures));
    }
}
