using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Devices.Enumeration;
using CameraLightTray.App.Platform;
using CameraLightTray.Core;

namespace CameraLightTray.App;

internal sealed class AppSession
{
    private readonly object _gate = new();
    private readonly Storage _storage;
    private readonly CancellationTokenSource _stop = new();
    private readonly LightAutomation _automation;
    private readonly AutomationDecision _decision = new();
    private Settings _settings;
    private MonitorSnapshot? _snapshot;
    private string _sensorMessage = "Waiting for Windows camera activity reports.";
    private Task? _loop, _monitor, _appDetails, _lightMaintenance;
    private readonly SemaphoreSlim _discovery = new(1, 1);
    private string? _lastLightError;
    private readonly SensorState _sensor = new();
    private ProcessIdentity[] _sensorProcesses = [];
    private int[] _sensorOtherOwners = [];
    private CameraDevice[] _sensorCameras = [];
    private int _sensorSession;
    private volatile bool _restartSensor;
    private CameraApp[] _catalog = [];
    private CameraApp[] _installedCameraApps = [];
    private readonly CameraPreview _preview;
    private readonly Dictionary<string, string> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _appNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lightReadings = new();
    internal event Action? Changed;
    internal int Revision { get; private set; }
    internal bool Isolated { get; }
    internal bool Demo { get; }
    internal string Error { get; private set; } = "";
    internal string SensorDetail { get; private set; } = "Monitor not connected; state is unknown.";
    internal string AutomationDetail { get; private set; } = "Automation is off.";
    internal string Notice { get; private set; } = "";
    internal string DiscoveryDetail { get; private set; } = "";
    internal bool Busy { get; private set; }
    internal ManualMode Mode { get; private set; }
    internal string AppPath { get; set; } = "";
    internal string LightAddress { get; set; } = "";
    internal string AppSearch { get; set; } = "";
    internal bool ShowAllApps { get; set; }
    internal int Page { get; set; }
    internal string PreviewCameraId { get; set; } = "";
    internal string PreviewDetail { get; private set; } = "Preview is stopped. It never records or uploads images.";
    internal string ActivationInput { get; set; } = "";
    internal string LeaveInput { get; set; } = "";
    internal LanAddress[] Interfaces { get; private set; } = [];
    internal CameraDevice[] Cameras { get; private set; } = [];
    internal string? IconPath(string appKey) { lock (_gate) return _icons.GetValueOrDefault(appKey); }
    internal string LightReading(string id) { lock (_gate) return _lightReadings.GetValueOrDefault(id, "Not read yet."); }
    internal Settings Preferences { get { lock (_gate) return _settings with { Apps = [.. _settings.Apps], Lights = [.. _settings.Lights], CameraIds = [.. _settings.CameraIds] }; } }
    internal CameraApp[] Apps { get { lock (_gate) return _installedCameraApps.UnionBy(_catalog, a => a.Key, StringComparer.OrdinalIgnoreCase)
        .UnionBy(_snapshot?.Apps ?? [], a => a.Key, StringComparer.OrdinalIgnoreCase)
        .UnionBy(_settings.Apps.Select(a => new CameraApp(a.Key, a.Name, CameraStatus.Inactive)), a => a.Key,
            StringComparer.OrdinalIgnoreCase)
        .Select(a => a with { Name = _appNames.GetValueOrDefault(a.Key, a.Name),
            Status = _snapshot?.Apps.FirstOrDefault(p => p.Key.Equals(a.Key, StringComparison.OrdinalIgnoreCase))?.Status ??
            (_snapshot?.Healthy == true ? CameraStatus.Inactive : CameraStatus.Unknown) })
        .OrderByDescending(a => _settings.Apps.Any(p => p.Enabled && p.Key.Equals(a.Key, StringComparison.OrdinalIgnoreCase)))
        .ThenByDescending(a => a.Status == CameraStatus.Active)
        .ThenByDescending(a => _installedCameraApps.Any(p => p.Key.Equals(a.Key, StringComparison.OrdinalIgnoreCase)) ||
            AppPriority.IsCameraOrCommunicationApp(a.Key))
        .ThenBy(a => a.Name).ToArray(); } }
    internal bool Suggested(CameraApp app) => Demo || app.Status == CameraStatus.Active ||
        Preferences.Apps.Any(a => a.Enabled && a.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase)) ||
        _installedCameraApps.Any(a => a.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase)) ||
        AppPriority.IsCameraOrCommunicationApp(app.Key);

    internal AppSession(Storage storage, bool isolated, bool demo)
    {
        _storage = storage; Isolated = isolated; Demo = demo;
        _preview = new(error => Post(() =>
        {
            Report(error);
            _ = StopPreviewAfterFailureAsync();
        }));
        _settings = storage.Load("settings.json", AppJson.Default.Settings) ?? new();
        _settings.Validate();
        ActivationInput = _settings.ActivationMilliseconds.ToString();
        LeaveInput = _settings.LeaveMilliseconds.ToString();
        ILightTransport transport = demo ? new DemoLights() : new ElgatoTransport();
        _automation = new(transport, journal => storage.Save("ownership.json", journal, AppJson.Default.OwnershipJournal));
        if (!demo) _automation.Recover(storage.Load("ownership.json", AppJson.Default.OwnershipJournal) ?? new([]));
        if (_automation.Owned.Count > 0) _decision.HoldRecoveredOwnership();
        // Isolated worktree launches must never inherit enabled automation from another run.
        if (isolated) _settings.AutomationEnabled = false;
        if (!demo) RefreshInterfaces();
        if (demo)
        {
            Interfaces = [new("demo", "Example LAN", "192.0.2.1", "255.255.255.0")];
            _settings.InterfaceId = "demo";
            _settings.Apps = [new("demo:meeting", "Example Meeting", true), new("demo:studio", "Example Studio", false)];
            _settings.Lights = [new("demo:light", "Studio Key Light (simulated)", "192.0.2.10", 9123, "demo", true)];
            _catalog = [new("demo:meeting", "Example Meeting", CameraStatus.Unknown), new("demo:studio", "Example Studio", CameraStatus.Unknown)];
            SensorDetail = "Synthetic demo. No ETW, setup, startup registration, discovery or physical light writes.";
        }
    }
    internal static void Post(Action action)
    {
        if (Microsoft.UI.Reactor.ReactorApp.UIDispatcher?.TryEnqueue(() => action()) != true)
            Trace.WriteLine("CameraLightTray UI dispatcher rejected an update.");
    }
    internal void Notify() { Revision++; Changed?.Invoke(); }
    internal void Report(string error) { Error = error; Notify(); }
    private void Save() => _storage.Save("settings.json", _settings, AppJson.Default.Settings);
    private void Change(Action<Settings> action)
    {
        try { lock (_gate) { action(_settings); _settings.Validate(); Save(); } Error = ""; Notify(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Report(ex.Message); }
    }
    internal void Start()
    {
        if (!Demo)
        {
            _monitor = Task.Run(() => MonitorSensorsAsync(_stop.Token));
            _appDetails = Task.Run(() => UpdateAppDetailsAsync(_stop.Token));
            _lightMaintenance = Task.Run(() => MaintainLightsAsync(_stop.Token));
        }
        _loop = RunAsync(_stop.Token);
    }
    private async Task MonitorSensorsAsync(CancellationToken token)
    {
        try { await ObserveSensorsAsync(token); }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            lock (_gate)
            {
                _snapshot = null;
                _sensorMessage = "Camera monitoring stopped unexpectedly: " + ex.Message + ". Camera state is unknown.";
                _sensor.Invalidate(_sensorMessage);
            }
            NotifySensor();
        }
    }
    private async Task ObserveSensorsAsync(CancellationToken token)
    {
        using var user = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        string sid = user.User!.Value;
        int session = process.SessionId;
        _sensorSession = session;
        await SensorMonitorRecovery.RunAsync(async cancellation =>
            {
                long epoch;
                bool catalogReady = false;
                DateTimeOffset catalogTime = default;
                string? reportError = null;
                lock (_gate)
                {
                    _restartSensor = false;
                    _sensor.Invalidate("Waiting for fresh camera activity reports and process identities.");
                    _sensorMessage = "Reconnecting Windows camera activity reports; camera state is unknown.";
                    epoch = _sensor.Epoch;
                    _snapshot = null;
                }
                NotifySensor();
                using var monitor = new SensorActivityMonitor(
                    reports =>
                    {
                        lock (_gate)
                        {
                            if (!_sensor.Receive(epoch, reports)) return;
                            _snapshot = catalogReady && DateTimeOffset.UtcNow - catalogTime < TimeSpan.FromSeconds(4) ?
                                UpdateSensorSnapshot() : null;
                            _sensorMessage = "Waiting for fresh process identities; camera state is unknown.";
                        }
                        NotifySensor();
                    },
                    error =>
                    {
                        lock (_gate)
                        {
                            if (epoch != _sensor.Epoch) return;
                            reportError = error;
                            _sensor.Invalidate(error); UpdateSensorSnapshot(); _restartSensor = true;
                        }
                        NotifySensor();
                    });
                monitor.Start();
                var observation = Stopwatch.StartNew();
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                int refresh = 0;
                CameraDevice[] cameras = [];
                do
                {
                    if (refresh++ % 5 == 0)
                    {
                        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask(cancellation);
                        cameras = devices.Select(d => new CameraDevice(d.Id, d.Name)).OrderBy(c => c.Name).ToArray();
                        var currentCameras = cameras;
                        Post(() =>
                        {
                            Cameras = currentCameras;
                            if (string.IsNullOrEmpty(PreviewCameraId)) PreviewCameraId = Cameras.FirstOrDefault()?.Id ?? "";
                            Notify();
                        });
                    }
                    var catalog = CameraLightTray.Monitor.ProcessCatalog.Enumerate(sid, session);
                    lock (_gate)
                    {
                        if (epoch != _sensor.Epoch) break;
                        if (!_sensor.Ready && observation.Elapsed > TimeSpan.FromSeconds(10))
                            throw new TimeoutException("Windows did not provide a usable camera activity report.");
                        if (observation.Elapsed >= TimeSpan.FromMinutes(1))
                        {
                            _snapshot = null;
                            _sensor.Invalidate("Refreshing the Windows camera observation.");
                            break;
                        }
                        _sensorProcesses = catalog.Processes; _sensorOtherOwners = catalog.OtherOwners; _sensorCameras = cameras;
                        catalogTime = DateTimeOffset.UtcNow;
                        catalogReady = true;
                        UpdateSensorSnapshot();
                    }
                    NotifySensor();
                } while (!_restartSensor && await timer.WaitForNextTickAsync(cancellation));
                if (reportError is not null) throw new IOException(reportError);
            },
            ex =>
            {
                Trace.WriteLine(ex);
                string error = ex.Message.TrimEnd('.');
                if (ex is Win32Exception native)
                    error += $" (Win32 {native.NativeErrorCode}: {new Win32Exception(native.NativeErrorCode).Message.TrimEnd('.')})";
                string detail = "Windows sensor monitor unavailable: " + error +
                    ". Retrying automatically in 2 seconds. Camera state is unknown.";
                lock (_gate) { _snapshot = null; _sensor.Invalidate(detail); _sensorMessage = detail; }
                NotifySensor();
            }, TimeSpan.FromSeconds(2), token);
    }
    private void NotifySensor() => Post(() =>
    {
        lock (_gate) SensorDetail = _snapshot?.Detail ?? _sensorMessage;
        Notify();
    });
    private MonitorSnapshot UpdateSensorSnapshot()
    {
        var excluded = _sensorOtherOwners.Concat(_sensor.StreamingProcesses.Where(pid =>
            CameraLightTray.Monitor.ProcessCatalog.IsOtherSession(pid, _sensorSession))).ToArray();
        return _snapshot = _sensor.Snapshot(_sensorProcesses, excluded, _sensorCameras,
            _settings.CameraIds, DateTimeOffset.UtcNow);
    }
    internal void SelectCamera(CameraDevice camera, bool enabled) => Change(s =>
    {
        s.CameraIds.RemoveAll(id => id.Equals(camera.Id, StringComparison.OrdinalIgnoreCase));
        if (enabled) s.CameraIds.Add(camera.Id);
    });
    internal void MonitorAllCameras() => Change(s => s.CameraIds.Clear());
    internal void AttachPreview(Microsoft.UI.Xaml.Controls.MediaPlayerElement element) => _preview.Attach(element);
    internal async Task StartPreviewAsync()
    {
        if (Demo) throw new InvalidOperationException("Physical camera preview is disabled in synthetic mode.");
        var camera = Cameras.FirstOrDefault(c => c.Id == PreviewCameraId) ??
            throw new InvalidOperationException("Choose an available camera for preview.");
        await _preview.StartAsync(camera.Id);
        PreviewDetail = _preview.Playing ? "Live local preview: " + camera.Name + ". No recording. Select CameraLightTray under Apps to test this preview's real sensor activity." :
            "Preview stopped.";
        Notify();
    }
    internal async Task StopPreviewAsync()
    {
        await _preview.StopAsync();
        PreviewDetail = "Preview stopped; camera stream released.";
        Notify();
    }
    private async Task StopPreviewAfterFailureAsync()
    {
        try { await StopPreviewAsync(); }
        catch (Exception ex) { Report("Preview cleanup failed: " + ex.Message); }
    }
    internal void HidePreview() => _ = StopPreviewAfterFailureAsync();
    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            do
            {
                var preferences = Preferences;
                MonitorSnapshot? snapshot;
                lock (_gate)
                {
                    if (Demo && _snapshot is not null) _snapshot = _snapshot with { Time = DateTimeOffset.UtcNow };
                    snapshot = _snapshot;
                }
                bool desired = _decision.Update(preferences, snapshot, Mode, DateTimeOffset.UtcNow);
                try
                {
                    await _automation.ReconcileAsync(desired, preferences.Lights, token);
                    Post(() =>
                    {
                        AutomationDetail = _automation.Status;
                        if (_lastLightError is not null && Error == _lastLightError) Error = "";
                        _lastLightError = null;
                        Notify();
                    });
                }
                catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or
                    OperationCanceledException or NotSupportedException or UnauthorizedAccessException)
                {
                    if (token.IsCancellationRequested) throw;
                    Post(() => { _lastLightError = "Light operation failed: " + ex.Message; Report(_lastLightError); });
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Post(() => Report("Automation worker stopped: " + ex.Message)); }
    }
    private async Task UpdateAppDetailsAsync(CancellationToken token)
    {
        await SensorMonitorRecovery.RunAsync(async cancellation =>
        {
            using var user = WindowsIdentity.GetCurrent();
            var installed = InstalledCameraApps.Read(user.User!.Value);
            lock (_gate) _installedCameraApps = installed;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            do
            {
                await CacheIconsAsync(Apps.Where(Suggested).Take(50).ToArray(), cancellation);
            } while (await timer.WaitForNextTickAsync(cancellation));
        }, ex =>
        {
            Trace.WriteLine(ex);
            Post(() =>
            {
                Notice = "Application details refresh failed; camera monitoring continues. Retrying: " + ex.Message;
                Notify();
            });
        }, TimeSpan.FromSeconds(30), token);
    }
    private async Task MaintainLightsAsync(CancellationToken token)
    {
        DateTimeOffset nextDiscovery = default;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        try
        {
            do
            {
                int failed = 0;
                var lights = Preferences.Lights.Where(l => l.Enabled && !l.Tls).ToArray();
                await Parallel.ForEachAsync(lights, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                    async (light, cancellation) =>
                    {
                        try { await ReadLightAsync(light, cancellation); }
                        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or
                            OperationCanceledException && !cancellation.IsCancellationRequested)
                        {
                            Interlocked.Increment(ref failed);
                            Trace.WriteLine(ex);
                        }
                    });
                if (failed > 0 && DateTimeOffset.UtcNow >= nextDiscovery)
                {
                    nextDiscovery = DateTimeOffset.UtcNow.AddSeconds(30);
                    try { await DiscoverAsync(); }
                    catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or
                        System.Net.Sockets.SocketException or System.Net.NetworkInformation.NetworkInformationException or
                        InvalidOperationException or OperationCanceledException && !token.IsCancellationRequested)
                    { Trace.WriteLine(ex); }
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Post(() => Report("Light reachability worker stopped: " + ex.Message)); }
    }
    private async Task CacheIconsAsync(CameraApp[] apps, CancellationToken token)
    {
        foreach (var app in apps)
        {
            if (!Path.IsPathFullyQualified(app.Key)) continue;
            try
            {
                bool named;
                lock (_gate) named = _appNames.ContainsKey(app.Key);
                if (!named)
                {
                    string name = FileVersionInfo.GetVersionInfo(app.Key).FileDescription is { Length: > 0 } friendly ? friendly : app.Name;
                    lock (_gate) _appNames[app.Key] = name;
                    Post(Notify);
                }
                if (IconPath(app.Key) is not null) continue;
                var file = await StorageFile.GetFileFromPathAsync(app.Key).AsTask(token);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 40,
                    ThumbnailOptions.UseCurrentScale).AsTask(token);
                if (thumbnail is null) continue;
                string directory = Path.Combine(_storage.DirectoryPath, "icons");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(app.Key))) + ".png");
                using var input = thumbnail.AsStreamForRead();
                await using (var output = File.Create(path)) await input.CopyToAsync(output, token);
                lock (_gate) _icons[app.Key] = path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                // Shell thumbnails are optional; retain the accessible application-glyph fallback.
                Trace.WriteLine("Application thumbnail unavailable: " + ex.GetType().Name);
            }
        }
    }
    internal void SelectApp(CameraApp app, bool enabled) => Change(s =>
    {
        s.Apps.RemoveAll(a => a.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase));
        s.Apps.Add(new(app.Key, app.Name, enabled));
    });
    internal void AddApp()
    {
        string path = AppPath.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        { Report("Enter the full path of an existing .exe, or select a running app below."); return; }
        string name = FileVersionInfo.GetVersionInfo(path).FileDescription ?? Path.GetFileNameWithoutExtension(path);
        SelectApp(new(path, name, CameraStatus.Unknown), true);
        AppPath = "";
    }
    internal void Enable(bool value) => Change(s => s.AutomationEnabled = value);
    internal void SetMode(ManualMode mode) { Mode = mode; Notify(); }
    internal void SelectLight(LightDevice light, bool enabled) => Change(s =>
    {
        s.Lights.RemoveAll(l => l.Id == light.Id);
        s.Lights.Add(light with { Enabled = enabled });
    });
    internal void SelectInterface(string id) => Change(s =>
    {
        s.InterfaceId = id;
        s.Lights = s.Lights.Select(light => light with { InterfaceId = id }).ToList();
    });
    internal void RefreshInterfaces()
    {
        try { Interfaces = LanInterfaces.Get(); }
        catch (System.Net.NetworkInformation.NetworkInformationException ex) { Error = ex.Message; }
        Notify();
    }
    internal void SaveRules()
    {
        if (!int.TryParse(ActivationInput, out int activation) || !int.TryParse(LeaveInput, out int leave) ||
            activation is < 100 or > 10000 || leave is < 500 or > 60000)
        { Report("Activation must be 100-10000ms; leave grace must be 500-60000ms."); return; }
        Change(s => { s.ActivationMilliseconds = activation; s.LeaveMilliseconds = leave; });
    }
    internal void Run(Func<Task> action)
    {
        if (Busy) return;
        Busy = true; Error = ""; Notify();
        _ = RunActionAsync(action);
    }
    private async Task RunActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Post(() => Report(ex.Message)); }
        finally { Post(() => { Busy = false; Notify(); }); }
    }
    internal async Task DiscoverAsync()
    {
        if (Demo) throw new InvalidOperationException("Discovery is disabled in synthetic mode.");
        await _discovery.WaitAsync(_stop.Token);
        try
        {
            Post(() => { DiscoveryDetail = "Looking for Elgato lights on the selected LAN interface..."; Notify(); });
            var interfaces = LanInterfaces.Get();
            Post(() => { Interfaces = interfaces; Notify(); });
            var lan = interfaces.FirstOrDefault(i => i.InterfaceId == Preferences.InterfaceId)
                ?? throw new InvalidOperationException("Choose a LAN interface first.");
            var found = await MdnsDiscovery.DiscoverAsync(lan, _stop.Token);
            var transport = new ElgatoTransport();
            var failures = new List<string>();
            for (int i = 0; i < found.Length; i++)
            {
                if (found[i].Tls) continue;
                try { found[i] = found[i] with { Name = await transport.IdentifyAsync(found[i], _stop.Token) }; }
                catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or
                    OperationCanceledException && !_stop.IsCancellationRequested)
                {
                    failures.Add($"{found[i].Name}: accessory identification failed ({ex.Message}). Use the read-only connection test.");
                }
            }
            Post(() =>
            {
                if (Preferences.InterfaceId != lan.InterfaceId)
                {
                    DiscoveryDetail = "LAN selection changed; discarded discovery from the previous interface.";
                    Notify();
                    return;
                }
                Change(s =>
                {
                    foreach (var light in found)
                    {
                        var old = s.Lights.FirstOrDefault(l => l.Id == light.Id);
                        s.Lights.RemoveAll(l => l.Id == light.Id);
                        s.Lights.Add(light with { Enabled = old?.Enabled ?? false });
                    }
                });
                DiscoveryDetail = found.Length == 0 ?
                    "No Elgato lights responded on this interface. Check that they are powered on and on the same LAN; you can also add a known IPv4 address below." :
                    $"Discovered {found.Length} light(s); new lights are not enabled automatically.";
                if (failures.Count > 0) DiscoveryDetail += " " + string.Join(" ", failures);
                Notify();
            });
        }
        catch (Exception ex)
        {
            Post(() => { DiscoveryDetail = "Discovery failed: " + ex.Message; Notify(); });
            throw;
        }
        finally { _discovery.Release(); }
    }
    internal async Task AddLightAsync()
    {
        if (Demo) throw new InvalidOperationException("Manual networking is disabled in synthetic mode.");
        var preferences = Preferences;
        string address = LightAddress.Trim();
        var device = new LightDevice("manual:" + address, address, address, 9123, preferences.InterfaceId);
        LightEndpoint.Validate(device);
        var transport = new ElgatoTransport();
        string name = await transport.IdentifyAsync(device, _stop.Token);
        await transport.ReadAsync(device, _stop.Token);
        Post(() => Change(s => { s.Lights.RemoveAll(l => l.Id == device.Id); s.Lights.Add(device with { Name = name }); }));
    }
    internal async Task TestLightAsync(LightDevice device)
    {
        if (Demo) { Notice = "Simulated light reachable."; Notify(); return; }
        var state = await ReadLightAsync(device, _stop.Token);
        Post(() => { Notice = $"{device.Name}: reachable, {(state.On == 1 ? "on" : "off")}, brightness {state.Brightness}%. Read-only test."; Notify(); });
    }
    private async Task<LightState> ReadLightAsync(LightDevice device, CancellationToken token)
    {
        try
        {
            var state = await new ElgatoTransport().ReadAsync(device, token);
            lock (_gate) _lightReadings[device.Id] = $"{(state.On == 1 ? "ON" : "OFF")} - brightness {state.Brightness}% - temperature {state.Temperature} (device readback)";
            Post(Notify);
            return state;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or
            OperationCanceledException && !token.IsCancellationRequested)
        {
            lock (_gate) _lightReadings[device.Id] = "Readback failed: " + ex.Message;
            Post(Notify);
            throw;
        }
    }
    internal void SetStartup(bool enabled)
    {
        if (Isolated || Demo) { Report("Login startup is disabled for isolated/demo runs."); return; }
        try { Setup.Startup(enabled); Change(s => s.StartAtLogin = enabled); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Report(ex.Message); }
    }
    internal async Task SetupAsync()
    {
        if (Isolated || Demo) throw new InvalidOperationException("Setup is disabled for isolated/demo runs.");
        await Setup.InstallAsync();
        Post(() =>
        {
            Notice = "Legacy helper setup completed. It is not the active Windows sensor source; setup success alone does not validate ETW or IPC health.";
            Notify();
        });
    }
    internal void Resume()
    {
        lock (_gate)
        {
            _snapshot = null;
            _sensorMessage = "System resumed; waiting for a fresh camera activity report.";
            _sensor.Invalidate(_sensorMessage);
            _restartSensor = true;
        }
        NotifySensor();
    }
    internal void DemoCapture(bool active)
    {
        lock (_gate) _snapshot = new(1, Revision + 1, 1, DateTimeOffset.UtcNow, true, "Synthetic capture.",
            [new("demo:meeting", "Example Meeting", active ? CameraStatus.Active : CameraStatus.Inactive)]);
        Notify();
    }
    internal async Task StopAsync()
    {
        var failures = new List<Exception>();
        _stop.Cancel();
        try { await StopPreviewAsync(); }
        catch (Exception ex) { failures.Add(ex); }
        try { await Task.WhenAll(_loop ?? Task.CompletedTask, _monitor ?? Task.CompletedTask,
            _appDetails ?? Task.CompletedTask, _lightMaintenance ?? Task.CompletedTask); }
        catch (Exception ex) { failures.Add(ex); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await _automation.ReconcileAsync(false, Preferences.Lights, timeout.Token); }
        catch (Exception ex) { failures.Add(ex); }
        if (failures.Count > 0) throw new AggregateException("Camera/light cleanup did not complete successfully.", failures);
    }
    private sealed class DemoLights : ILightTransport
    {
        private LightState _state = new(0, 40, 250);
        public Task<LightState> ReadAsync(LightDevice device, CancellationToken cancellationToken) => Task.FromResult(_state);
        public Task WriteAsync(LightDevice device, LightState state, CancellationToken cancellationToken)
        { _state = state; return Task.CompletedTask; }
    }
}
