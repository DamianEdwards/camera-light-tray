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
        if (!settings.Apps.Any(a => a.Enabled))
        {
            _activeSince = _inactiveSince = null;
            return Desired = false;
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

public sealed class LightAutomation(ILightTransport transport, Action<OwnershipJournal> persist, TimeProvider? clock = null)
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _reconcile = new(1, 1);
    private readonly Dictionary<string, OwnedLight> _owned = [];
    private readonly HashSet<string> _suppressed = [];
    private readonly Dictionary<string, Attempt> _attempts = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private bool _dirty;
    private bool _wasDesired;
    private sealed record Attempt(LightDevice Device, bool Desired, int Failures, DateTimeOffset Next, string Error);
    public IReadOnlyCollection<OwnedLight> Owned { get { lock (_gate) return _owned.Values.ToArray(); } }
    public bool IsManual(string id) { lock (_gate) return _suppressed.Contains(id); }
    public string Status { get; private set; } = "Automation is off.";
    public void Recover(OwnershipJournal journal)
    {
        lock (_gate) foreach (var item in journal.Lights) _owned.TryAdd(item.Device.Id, item);
    }
    private void Save()
    {
        try { persist(new(_owned.Values.ToList())); _dirty = false; }
        catch { _dirty = true; throw; }
    }
    private void Store(OwnedLight item)
    {
        lock (_gate) { _owned[item.Device.Id] = item; Save(); }
    }
    private void Release(string id, bool suppress)
    {
        lock (_gate)
        {
            _owned.Remove(id);
            if (suppress) _suppressed.Add(id);
            Save();
        }
    }

    public async Task ReconcileAsync(bool desired, IEnumerable<LightDevice> devices, CancellationToken token)
    {
        await _reconcile.WaitAsync(token);
        try { await ReconcileDevicesAsync(desired, devices, token); }
        finally { _reconcile.Release(); }
    }

    public async Task<LightState> AdjustAsync(LightDevice device, int? on, int? brightness, int? kelvin, CancellationToken token)
    {
        await _reconcile.WaitAsync(token);
        try
        {
            lock (_gate) if (_dirty) Save();
            var current = await transport.ReadAsync(device, token);
            var next = LightControls.Adjust(current, on, brightness, kelvin);
            lock (_gate) _suppressed.Add(device.Id);
            // Keep the old ownership intent until a write is acknowledged. A lost
            // acknowledgment is resolved by readback, never by fighting the edit.
            if (next != current) await transport.WriteAsync(device, next, token);
            Release(device.Id, true);
            lock (_gate) _attempts.Remove(device.Id);
            Status = "Manual light adjustment. Resume auto to return control.";
            var observed = await transport.ReadAsync(device, token);
            if (observed != next) throw new IOException("The light's readback does not match the requested adjustment.");
            return observed;
        }
        finally { _reconcile.Release(); }
    }

    public async Task ResumeAsync(string id, CancellationToken token)
    {
        await _reconcile.WaitAsync(token);
        try
        {
            lock (_gate)
            {
                if (_dirty) Save();
                // An explicit resume starts a new baseline from the current state.
                if (_owned.ContainsKey(id)) Release(id, false);
                _suppressed.Remove(id);
                _attempts.Remove(id);
            }
        }
        finally { _reconcile.Release(); }
    }

    private async Task ReconcileDevicesAsync(bool desired, IEnumerable<LightDevice> devices, CancellationToken token)
    {
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var configured = devices.ToDictionary(d => d.Id);
        string[] ids;
        lock (_gate)
        {
            if (_dirty) Save();
            if (!desired && _wasDesired) _suppressed.Clear();
            _wasDesired = desired;
            ids = _owned.Keys.Union(configured.Values.Where(d => desired && d.Enabled && !d.Tls).Select(d => d.Id)).ToArray();
            foreach (string id in _attempts.Keys.Except(ids).ToArray()) _attempts.Remove(id);
        }
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (id, cancellation) =>
        {
            OwnedLight? item;
            Attempt? previous;
            LightDevice device;
            lock (_gate)
            {
                _owned.TryGetValue(id, out item);
                device = configured.GetValueOrDefault(id) ?? item!.Device;
                if (_suppressed.Contains(id)) return;
                _attempts.TryGetValue(id, out previous);
                if (previous is not null && (previous.Device != device || previous.Desired != desired)) previous = null;
                if (previous is not null && previous.Device == device && previous.Desired == desired && previous.Next > _clock.GetUtcNow())
                {
                    if (previous.Failures > 0) failures.Enqueue(new IOException($"{device.Name}: {previous.Error} Retrying automatically."));
                    return;
                }
            }
            try
            {
                if (item is not null)
                {
                    if (item.Device != device) { item = item with { Device = device }; Store(item); }
                    var current = await transport.ReadAsync(device, cancellation);
                    if (!item.AppliedConfirmed && current == item.Applied)
                    {
                        item = item with { AppliedConfirmed = true };
                        Store(item);
                    }
                    if (!item.AppliedConfirmed && current == item.Before)
                    {
                        if (desired && device.Enabled && !device.Tls)
                        {
                            Store(item);
                            await transport.WriteAsync(device, item.Applied, cancellation);
                            Store(item with { AppliedConfirmed = true });
                            Status = "Camera activity: lights on (owned changes only).";
                        }
                        else
                        {
                            Release(id, false);
                            Status = "Previous light state unchanged; pending activation released.";
                        }
                    }
                    else if (current != item.Applied)
                    {
                        Release(id, desired);
                        Status = "External light change detected; leaving it alone until this activation ends.";
                    }
                    else if (!desired || !device.Enabled || device.Tls || !configured.ContainsKey(id))
                    {
                        Store(item);
                        await transport.WriteAsync(device, item.Before, cancellation);
                        Release(id, false);
                        Status = "Previous light state restored.";
                    }
                }
                else
                {
                    var before = await transport.ReadAsync(device, cancellation);
                    var applied = before with { On = 1 };
                    if (before != applied)
                    {
                        item = new(device, before, applied, AppliedConfirmed: false);
                        Store(item);
                        await transport.WriteAsync(device, applied, cancellation);
                        Store(item with { AppliedConfirmed = true });
                    }
                    Status = "Camera activity: lights on (owned changes only).";
                }
                lock (_gate) _attempts[id] = new(device, desired, 0, _clock.GetUtcNow().AddSeconds(1), "");
            }
            catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or
                NotSupportedException or OperationCanceledException && !cancellation.IsCancellationRequested)
            {
                int count = Math.Min((previous?.Failures ?? 0) + 1, 5);
                lock (_gate) _attempts[id] = new(device, desired, count,
                    _clock.GetUtcNow().AddSeconds(Math.Min(30, 1 << count)), ex.Message);
                failures.Enqueue(new IOException($"{device.Name}: {ex.Message} Retrying automatically.", ex));
            }
        });
        if (failures.Count > 0) throw new IOException(
            $"{failures.Count} light operation(s) failed. {string.Join(" ", failures.Select(e => e.Message))}",
            new AggregateException(failures));
    }
}
