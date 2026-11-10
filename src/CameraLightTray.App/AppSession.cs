using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Devices.Enumeration;
using CameraLightTray.App.Platform;
using CameraLightTray.Core;
using CameraLightTray.App.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using static CameraLightTray.App.Diagnostics.LogIdentity;

namespace CameraLightTray.App;

internal sealed record LightObservation(LightState? State, DateTimeOffset Time, string? Error = null)
{
    internal bool Fresh => State is not null && Error is null && DateTimeOffset.UtcNow - Time < TimeSpan.FromSeconds(12);
}

internal sealed class AppSession
{
    private readonly object _gate = new();
    private readonly Storage _storage;
    private readonly ILogger _log;
    internal static ILogger DispatchLog { get; set; } = NullLogger.Instance;
    private string? _activityLogKey, _decisionLogKey;
    private DateTimeOffset _lastCallback, _lastCatalog, _sensorOperationStarted;
    private string _sensorOperation = "not-started";
    private long _callbackCount;
    private string? _callbackSummary, _loggingFailure;
    private readonly CancellationTokenSource _stop = new();
    private readonly LightAutomation _automation;
    private readonly ILightTransport _transport;
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
    private readonly Dictionary<string, LightObservation> _observations = new();
    private readonly Dictionary<string, (int? Brightness, int? Kelvin, CancellationTokenSource Stop)> _edits = new();
    private readonly List<Task> _editing = [];
    internal Action? OpenSettings { get; set; }
    internal event Action? Changed;
    internal event Action? ShellChanged;
    internal int ShellRevision { get; private set; }
    internal int SettingsShellRenders { get; set; }
    internal Action<double>? FlyoutMeasured { get; set; }
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
    private int _page;
    internal int Page
    {
        get => _page;
        set
        {
            if (_page == value) return;
            _page = value;
            RefreshShell();
        }
    }
    private void RefreshShell() { ShellRevision++; ShellChanged?.Invoke(); Notify(); }
    internal void SetAppearance(AppearanceMode mode)
    {
        Change(settings => settings.Appearance = mode);
        UI.AppTheme.Select(Preferences.Appearance);
    }
    internal string PreviewCameraId { get; set; } = "";
    internal string PreviewDetail { get; private set; } = "Preview is stopped. It never records or uploads images.";
    internal string ActivationInput { get; set; } = "";
    internal string LeaveInput { get; set; } = "";
    internal LanAddress[] Interfaces { get; private set; } = [];
    internal CameraDevice[] Cameras { get; private set; } = [];
    internal string? IconPath(string appKey) { lock (_gate) return _icons.GetValueOrDefault(appKey); }
    internal string LightReading(string id) { lock (_gate) return _lightReadings.GetValueOrDefault(id, "Not read yet."); }
    internal LightObservation Observation(string id) { lock (_gate) return _observations.GetValueOrDefault(id, new(null, default)); }
    internal bool IsManual(string id) => _automation.IsManual(id);
    internal bool IsEditing(string id) { lock (_gate) return _edits.ContainsKey(id); }
    internal string ActivityTitle
    {
        get
        {
            lock (_gate) return ActivityStatus.Evaluate(_settings, _snapshot, Mode, DateTimeOffset.UtcNow).Title;
        }
    }
    internal string ActivityContext
    {
        get
        {
            var settings = Preferences;
            if (!settings.Apps.Any(a => a.Enabled)) return "No selected apps will trigger lighting";
            lock (_gate)
            {
                var active = _snapshot?.Apps.Where(a => a.Status == CameraStatus.Active &&
                    settings.Apps.Any(s => s.Enabled && s.Key.Equals(a.Key, StringComparison.OrdinalIgnoreCase))).Select(a => a.Name).ToArray() ?? [];
                int count = settings.Apps.Count(a => a.Enabled);
                return active.Length > 0 ? string.Join(", ", active) : $"{count} selected {(count == 1 ? "app" : "apps")} · " +
                    (settings.CameraIds.Count == 0 ? "all cameras" : $"{settings.CameraIds.Count} selected {(settings.CameraIds.Count == 1 ? "camera" : "cameras")}");
            }
        }
    }
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

    internal AppSession(Storage storage, bool isolated, bool demo, ILoggerFactory logging)
    {
        _storage = storage; Isolated = isolated; Demo = demo;
        _log = logging.CreateLogger<AppSession>();
        _preview = new(error => Post(() =>
        {
            Report(error);
            _ = StopPreviewAfterFailureAsync();
        }));
        _settings = storage.Load("settings.json", AppJson.Default.Settings) ?? new();
        _settings.Validate();
        ActivationInput = _settings.ActivationMilliseconds.ToString();
        LeaveInput = _settings.LeaveMilliseconds.ToString();
        _transport = new LoggingLightTransport(demo ? new DemoLights() : new ElgatoTransport(),
            logging.CreateLogger<LoggingLightTransport>());
        _automation = new(_transport, journal =>
        {
            storage.Save("ownership.json", journal, AppJson.Default.OwnershipJournal);
            _log.LogInformation("Ownership journal saved; ownedLights={OwnedLights}, lights={Lights}",
                journal.Lights.Count, string.Join(",", journal.Lights.Select(l => Token(l.Device.Id))));
        });
        if (!demo) _automation.Recover(storage.Load("ownership.json", AppJson.Default.OwnershipJournal) ?? new([]));
        if (_automation.Owned.Count > 0) _decision.HoldRecoveredOwnership();
        _log.LogInformation("Preferences loaded; enabled={Enabled}, selectedApps={SelectedApps}, selectedCameras={SelectedCameras}, lights={Lights}, recoveredOwnership={OwnedLights}, isolated={Isolated}, demo={Demo}",
            _settings.AutomationEnabled, _settings.Apps.Count(a => a.Enabled), _settings.CameraIds.Count,
            _settings.Lights.Count, _automation.Owned.Count, isolated, demo);
        // Isolated worktree launches must never inherit enabled automation from another run.
        if (isolated) _settings.AutomationEnabled = false;
        if (!demo) RefreshInterfaces();
        if (demo)
        {
            Interfaces = [new("demo", "Example LAN", "192.0.2.1", "255.255.255.0")];
            _settings.InterfaceId = "demo";
            _settings.Apps = [new("demo:meeting", "Example Meeting", true), new("demo:studio", "Example Studio", false)];
            _settings.Lights = [new("demo:light", "Key Light", "192.0.2.10", 9123, "demo", true) { ProductName = "Elgato Key Light" },
                new("demo:air", "Key Light Air", "192.0.2.11", 9123, "demo", true) { ProductName = "Elgato Key Light Air" }];
            Cameras = [new("demo:camera", "Example camera")];
            _catalog = [new("demo:meeting", "Example Meeting", CameraStatus.Unknown), new("demo:studio", "Example Studio", CameraStatus.Unknown)];
            SensorDetail = "Synthetic demo. No ETW, setup, startup registration, discovery or physical light writes.";
        }
    }
    internal static void Post(Action action)
    {
        if (Microsoft.UI.Reactor.ReactorApp.UIDispatcher?.TryEnqueue(() => action()) != true)
            DispatchLog.LogWarning("UI dispatcher rejected an update");
    }
    internal void Notify() { LogActivity(); Revision++; Changed?.Invoke(); }
    internal void Report(string error)
    {
        if (error.Length > 0 && Error != error) _log.LogWarning("User-visible operation error: {Error}", error);
        Error = error; Notify();
    }
    internal void SetLoggingFailure(string? failure)
    {
        if (_loggingFailure is not null && Notice == _loggingFailure) Notice = "";
        _loggingFailure = failure;
        if (failure is not null) Notice = failure;
        Revision++; Changed?.Invoke();
    }
    private void LogActivity()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var status = ActivityStatus.Evaluate(_settings, _snapshot, Mode, now);
            string detail = _snapshot?.Detail ?? _sensorMessage;
            string key = $"{status.Code}|{detail}|{_sensor.Epoch}";
            if (_activityLogKey == key) return;
            _log.LogInformation("Activity transition {Previous} -> {Reason}; title={Title}, healthy={Healthy}, snapshotAgeMs={SnapshotAgeMs}, sequence={Sequence}, epoch={Epoch}, callbackAgeMs={CallbackAgeMs}, catalogAgeMs={CatalogAgeMs}, operation={Operation}, operationAgeMs={OperationAgeMs}, detail={Detail}",
                _activityLogKey, status.Code, status.Title, _snapshot?.Healthy,
                _snapshot is null ? null : (double?)(now - _snapshot.Time).TotalMilliseconds, _snapshot?.Sequence, _sensor.Epoch,
                _lastCallback == default ? null : (double?)(now - _lastCallback).TotalMilliseconds,
                _lastCatalog == default ? null : (double?)(now - _lastCatalog).TotalMilliseconds,
                _sensorOperation, _sensorOperationStarted == default ? null : (double?)(now - _sensorOperationStarted).TotalMilliseconds, detail);
            _activityLogKey = key;
            _log.LogInformation("Activity inputs; selectedCameras={SelectedCameras}, availableCameras={AvailableCameras}, unmatchedStreamingPids={UnmatchedStreamingPids}, selectedActive={SelectedActive}, selectedUnknown={SelectedUnknown}, selectedAppStates={SelectedAppStates}",
                _settings.CameraIds.Count == 0 ? "all" : string.Join(",", _settings.CameraIds.Select(Token)),
                string.Join(",", _sensorCameras.Select(c => Token(c.Id))),
                string.Join(",", _sensor.StreamingProcesses.Except(_sensorProcesses.Select(p => p.Pid)).Except(_sensorOtherOwners)),
                _snapshot?.Apps.Count(a => a.Status == CameraStatus.Active && _settings.Apps.Any(s => s.Enabled && s.Key.Equals(a.Key, StringComparison.OrdinalIgnoreCase))),
                _snapshot?.Apps.Count(a => a.Status == CameraStatus.Unknown && _settings.Apps.Any(s => s.Enabled && s.Key.Equals(a.Key, StringComparison.OrdinalIgnoreCase))),
                string.Join(",", _settings.Apps.Where(a => a.Enabled).Select(a => $"{Token(a.Key)}:{_snapshot?.Apps.FirstOrDefault(p => p.Key.Equals(a.Key, StringComparison.OrdinalIgnoreCase))?.Status}")));
        }
    }
    private void Save() => _storage.Save("settings.json", _settings, AppJson.Default.Settings);
    private void Change(Action<Settings> action, [CallerMemberName] string operation = "")
    {
        try
        {
            lock (_gate)
            {
                action(_settings); _settings.Validate(); Save();
                _log.LogInformation("Settings saved; operation={Operation}, enabled={Enabled}, selectedApps={SelectedApps}, selectedCameras={SelectedCameras}, autoLights={AutoLights}, debounceMs={DebounceMs}, graceMs={GraceMs}, appTokens={AppTokens}, cameraTokens={CameraTokens}",
                    operation, _settings.AutomationEnabled, _settings.Apps.Count(a => a.Enabled), _settings.CameraIds.Count,
                    _settings.Lights.Count(l => l.Enabled), _settings.ActivationMilliseconds, _settings.LeaveMilliseconds,
                    string.Join(",", _settings.Apps.Where(a => a.Enabled).Select(a => Token(a.Key))), string.Join(",", _settings.CameraIds.Select(Token)));
            }
            Error = ""; Notify();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { _log.LogError(ex, "Settings operation failed {Operation}", operation); Report(ex.Message); }
    }
    internal void Start()
    {
        _log.LogInformation("Session workers starting");
        UI.AppTheme.Start(Preferences.Appearance, RefreshShell);
        if (!Demo)
        {
            _monitor = Task.Run(() => MonitorSensorsAsync(_stop.Token));
            _appDetails = Task.Run(() => UpdateAppDetailsAsync(_stop.Token));
        }
        _lightMaintenance = Task.Run(() => MaintainLightsAsync(_stop.Token));
        _loop = RunAsync(_stop.Token);
    }
    private async Task MonitorSensorsAsync(CancellationToken token)
    {
        try { await ObserveSensorsAsync(token); }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Camera monitoring worker stopped unexpectedly");
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
                _log.LogInformation("Sensor observation starting; epoch={Epoch}", epoch);
                NotifySensor();
                using var monitor = new SensorActivityMonitor(
                    reports =>
                    {
                        lock (_gate)
                        {
                            if (!_sensor.Receive(epoch, reports))
                            {
                                _log.LogInformation("Ignored retired sensor callback; callbackEpoch={CallbackEpoch}, currentEpoch={Epoch}", epoch, _sensor.Epoch);
                                return;
                            }
                            _lastCallback = DateTimeOffset.UtcNow;
                            _callbackCount++;
                            string summary = string.Join(";", reports.Select(r => $"{Token(r.Id)}:clients={r.Processes.Length}:streaming={string.Join(",", r.Processes.Where(p => p.Streaming).Select(p => p.Pid).Order())}"));
                            if (_callbackSummary != summary)
                            {
                                _log.LogInformation("Sensor activity report changed; epoch={Epoch}, reports={Reports}, cameras={Cameras}", epoch, reports.Length, summary);
                                _callbackSummary = summary;
                            }
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
                            _log.LogWarning("Sensor callback failed; epoch={Epoch}, error={Error}", epoch, error);
                            _sensor.Invalidate(error); UpdateSensorSnapshot(); _restartSensor = true;
                        }
                        NotifySensor();
                    });
                SetSensorOperation("start-observation");
                monitor.Start();
                var observation = Stopwatch.StartNew();
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                int refresh = 0;
                CameraDevice[] cameras = [];
                do
                {
                    if (refresh++ % 5 == 0)
                    {
                        SetSensorOperation("enumerate-cameras");
                        var started = Stopwatch.StartNew();
                        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask(cancellation);
                        cameras = devices.Select(d => new CameraDevice(d.Id, d.Name)).OrderBy(c => c.Name).ToArray();
                        if (refresh == 1 || started.ElapsedMilliseconds >= 500)
                            _log.LogInformation("Camera enumeration completed; cameras={Cameras}, elapsedMs={ElapsedMs}", cameras.Length, started.ElapsedMilliseconds);
                        var currentCameras = cameras;
                        Post(() =>
                        {
                            Cameras = currentCameras;
                            if (string.IsNullOrEmpty(PreviewCameraId)) PreviewCameraId =
                                Cameras.FirstOrDefault(c => Preferences.CameraIds.Contains(c.Id, StringComparer.OrdinalIgnoreCase))?.Id ??
                                Cameras.FirstOrDefault()?.Id ?? "";
                            Notify();
                        });
                    }
                    SetSensorOperation("process-catalog");
                    var catalogStarted = Stopwatch.StartNew();
                    var catalog = CameraLightTray.Monitor.ProcessCatalog.Enumerate(sid, session);
                    lock (_gate)
                    {
                        if (epoch != _sensor.Epoch) break;
                        if (!_sensor.Ready && observation.Elapsed > TimeSpan.FromSeconds(10))
                            throw new TimeoutException("Windows did not provide a usable camera activity report.");
                        if (observation.Elapsed >= TimeSpan.FromMinutes(1))
                        {
                            _log.LogInformation("Renewing sensor observation after one minute; epoch={Epoch}, callbacks={Callbacks}", epoch, _callbackCount);
                            _snapshot = null;
                            _sensor.Invalidate("Refreshing the Windows camera observation.");
                            break;
                        }
                        _sensorProcesses = catalog.Processes; _sensorOtherOwners = catalog.OtherOwners; _sensorCameras = cameras;
                        catalogTime = DateTimeOffset.UtcNow;
                        _lastCatalog = catalogTime;
                        _sensorOperation = "waiting";
                        _sensorOperationStarted = catalogTime;
                        catalogReady = true;
                        var currentSnapshot = UpdateSensorSnapshot();
                        if (refresh % 30 == 1 || catalogStarted.ElapsedMilliseconds >= 500)
                            _log.LogInformation("Sensor heartbeat; epoch={Epoch}, callbacks={Callbacks}, callbackAgeMs={CallbackAgeMs}, catalogMs={CatalogMs}, processes={Processes}, excludedOwners={ExcludedOwners}, healthy={Healthy}",
                                epoch, _callbackCount, _lastCallback == default ? null : (double?)(catalogTime - _lastCallback).TotalMilliseconds,
                                catalogStarted.ElapsedMilliseconds, catalog.Processes.Length, catalog.OtherOwners.Length, currentSnapshot.Healthy);
                    }
                    NotifySensor();
                } while (!_restartSensor && await timer.WaitForNextTickAsync(cancellation));
                SetSensorOperation("stop-observation");
                if (reportError is not null) throw new IOException(reportError);
            },
            ex =>
            {
                _log.LogWarning(ex, "Sensor monitor failed; retryMs={RetryMs}", 2000);
                string error = ex.Message.TrimEnd('.');
                if (ex is Win32Exception native)
                    error += $" (Win32 {native.NativeErrorCode}: {new Win32Exception(native.NativeErrorCode).Message.TrimEnd('.')})";
                string detail = "Windows sensor monitor unavailable: " + error +
                    ". Retrying automatically in 2 seconds. Camera state is unknown.";
                lock (_gate) { _snapshot = null; _sensor.Invalidate(detail); _sensorMessage = detail; }
                NotifySensor();
            }, TimeSpan.FromSeconds(2), token);
    }
    private void SetSensorOperation(string operation)
    {
        lock (_gate) { _sensorOperation = operation; _sensorOperationStarted = DateTimeOffset.UtcNow; }
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
        _log.LogInformation("Camera preview starting; camera={Camera}", Token(camera.Id));
        await _preview.StartAsync(camera.Id);
        _log.LogInformation("Camera preview start completed; playing={Playing}", _preview.Playing);
        PreviewDetail = _preview.Playing ? "Live local preview: " + camera.Name + ". No recording. Select CameraLightTray under Apps to test this preview's real sensor activity." :
            "Preview stopped.";
        Notify();
    }
    internal async Task StopPreviewAsync()
    {
        bool playing = _preview.Playing;
        await _preview.StopAsync();
        if (playing) _log.LogInformation("Camera preview stopped and stream released");
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
                LogActivity();
                string decisionKey = $"{desired}|{snapshot?.Healthy}|{Mode}|{preferences.AutomationEnabled}";
                if (_decisionLogKey != decisionKey)
                {
                    _log.LogInformation("Lighting decision changed; desired={Desired}, healthy={Healthy}, mode={Mode}, enabled={Enabled}, ownedLights={OwnedLights}",
                        desired, snapshot?.Healthy, Mode, preferences.AutomationEnabled, _automation.Owned.Count);
                    _decisionLogKey = decisionKey;
                }
                try
                {
                    using var operation = _log.BeginScope("operation=automation-reconcile desired={Desired}", desired);
                    await _automation.ReconcileAsync(desired, preferences.Lights, token);
                    Post(() =>
                    {
                        bool changed = AutomationDetail != _automation.Status || _lastLightError is not null;
                        AutomationDetail = _automation.Status;
                        if (changed) _log.LogInformation("Automation outcome: {Status}; ownedLights={OwnedLights}", AutomationDetail, _automation.Owned.Count);
                        if (_lastLightError is not null && Error == _lastLightError) Error = "";
                        _lastLightError = null;
                        if (changed) Notify();
                    });
                }
                catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or
                    OperationCanceledException or NotSupportedException or UnauthorizedAccessException)
                {
                    if (token.IsCancellationRequested) throw;
                    if (_lastLightError != "Light operation failed: " + ex.Message)
                        _log.LogError(ex, "Automation reconciliation failed; recovery intent retained");
                    Post(() => { _lastLightError = "Light operation failed: " + ex.Message; Report(_lastLightError); });
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { _log.LogCritical(ex, "Automation worker stopped"); Post(() => Report("Automation worker stopped: " + ex.Message)); }
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
            _log.LogWarning(ex, "Application details refresh failed; retrySeconds={RetrySeconds}", 30);
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
                var lights = Preferences.Lights.Where(l => !l.Tls).ToArray();
                await Parallel.ForEachAsync(lights, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                    async (light, cancellation) =>
                    {
                        try
                        {
                            await ReadLightAsync(light, cancellation);
                            if (!Demo && light.ProductName is null)
                            {
                                var info = await new ElgatoTransport().IdentifyDetailsAsync(light, cancellation);
                                Post(() => Change(s => s.Lights = s.Lights.Select(l => l.Id == light.Id ?
                                    l with { ProductName = info.ProductName ?? "" } : l).ToList()));
                            }
                        }
                        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or
                            OperationCanceledException && !cancellation.IsCancellationRequested)
                        {
                            Interlocked.Increment(ref failed);
                            if (light.ProductName is null) _log.LogWarning(ex, "Light metadata/readback refresh failed {Light}", light.Name);
                        }
                    });
                if (!Demo && failed > 0 && DateTimeOffset.UtcNow >= nextDiscovery)
                {
                    nextDiscovery = DateTimeOffset.UtcNow.AddSeconds(30);
                    try { await DiscoverAsync(); }
                    catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or
                        System.Net.Sockets.SocketException or System.Net.NetworkInformation.NetworkInformationException or
                        InvalidOperationException or OperationCanceledException && !token.IsCancellationRequested)
                    { _log.LogWarning(ex, "Automatic light rediscovery failed; retrySeconds={RetrySeconds}", 30); }
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { _log.LogCritical(ex, "Light reachability worker stopped"); Post(() => Report("Light reachability worker stopped: " + ex.Message)); }
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
                _log.LogDebug(ex, "Optional application thumbnail unavailable");
            }
        }
    }
    internal void SelectApp(CameraApp app, bool enabled) => Change(s =>
    {
        s.Apps.RemoveAll(a => a.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase));
        s.Apps.Add(new(app.Key, app.Name, enabled));
    });
    internal void ClearApps() => Change(s => s.Apps = s.Apps.Select(a => a with { Enabled = false }).ToList());
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
    internal void SetMode(ManualMode mode)
    {
        _log.LogInformation("Automation override changed {Previous} -> {Mode}", Mode, mode);
        Mode = mode; Notify();
    }
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
    internal void AdjustLight(LightDevice light, int? brightness = null, int? kelvin = null)
    {
        CancellationTokenSource stop;
        lock (_gate)
        {
            _edits.TryGetValue(light.Id, out var previous);
            previous.Stop?.Cancel();
            stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _edits[light.Id] = (brightness ?? previous.Brightness, kelvin ?? previous.Kelvin, stop);
            _editing.RemoveAll(task => task.IsCompleted);
            _editing.Add(ApplyLightEditAsync(light, stop));
        }
        Notify();
    }
    private async Task ApplyLightEditAsync(LightDevice light, CancellationTokenSource stop)
    {
        try
        {
            await Task.Delay(250, stop.Token);
            (int? Brightness, int? Kelvin, CancellationTokenSource Stop) edit;
            lock (_gate) edit = _edits[light.Id];
            _log.LogInformation("Manual light adjustment; light={Light}, brightness={Brightness}, kelvin={Kelvin}", light.Name, edit.Brightness, edit.Kelvin);
            var state = await AdjustAndReadAsync(light, null, edit.Brightness, edit.Kelvin, stop.Token);
            RememberLight(light, state);
            Post(Notify);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { _log.LogError(ex, "Manual light adjustment failed {Light}", light.Name); Post(() => Report("Light adjustment failed: " + ex.Message)); }
        finally
        {
            lock (_gate) if (_edits.TryGetValue(light.Id, out var current) && current.Stop == stop) _edits.Remove(light.Id);
            stop.Dispose();
            Post(Notify);
        }
    }
    internal async Task ToggleLightAsync(LightDevice light)
    {
        var reading = Observation(light.Id);
        if (!reading.Fresh) throw new IOException("This light has no fresh power reading. Test its connection before changing it.");
        _log.LogInformation("Manual power toggle {Light}; previousOn={On}", light.Name, reading.State!.On);
        var state = await AdjustAndReadAsync(light, reading.State!.On == 1 ? 0 : 1, null, null, _stop.Token);
        RememberLight(light, state);
        Post(Notify);
    }
    private async Task<LightState> AdjustAndReadAsync(LightDevice light, int? on, int? brightness, int? kelvin, CancellationToken token)
    {
        using var operation = _log.BeginScope("operation=manual-adjust");
        try { return await _automation.AdjustAsync(light, on, brightness, kelvin, token); }
        catch (Exception ex)
        {
            lock (_gate) _observations[light.Id] = Observation(light.Id) with { Error = ex.Message };
            throw;
        }
    }
    internal async Task ResumeLightAsync(LightDevice light)
    {
        _log.LogInformation("Resume automatic control for {Light}", light.Name);
        lock (_gate) if (_edits.TryGetValue(light.Id, out var pending)) pending.Stop.Cancel();
        await _automation.ResumeAsync(light.Id, _stop.Token);
        Post(Notify);
    }
    internal void RefreshInterfaces()
    {
        try { Interfaces = LanInterfaces.Get(); _log.LogInformation("LAN interfaces refreshed; count={Count}", Interfaces.Length); }
        catch (System.Net.NetworkInformation.NetworkInformationException ex) { _log.LogWarning(ex, "LAN interface refresh failed"); Error = ex.Message; }
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
        catch (Exception ex) { _log.LogError(ex, "User-requested operation failed"); Post(() => Report(ex.Message)); }
        finally { Post(() => { Busy = false; Notify(); }); }
    }
    internal async Task DiscoverAsync()
    {
        if (Demo) throw new InvalidOperationException("Discovery is disabled in synthetic mode.");
        await _discovery.WaitAsync(_stop.Token);
        var started = Stopwatch.StartNew();
        _log.LogInformation("Light discovery starting; interface={Interface}", Token(Preferences.InterfaceId));
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
                try
                {
                    var info = await transport.IdentifyDetailsAsync(found[i], _stop.Token);
                    found[i] = found[i] with { Name = info.DisplayName ?? info.ProductName ?? found[i].Name, ProductName = info.ProductName };
                }
                catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or
                    OperationCanceledException && !_stop.IsCancellationRequested)
                {
                    _log.LogWarning(ex, "Discovered light identification failed {Light}", found[i].Name);
                    failures.Add($"{found[i].Name}: accessory identification failed ({ex.Message}). Use the read-only connection test.");
                }
            }
            _log.LogInformation("Light discovery completed; found={Found}, tlsUnsupported={TlsUnsupported}, identificationFailures={Failures}, elapsedMs={ElapsedMs}",
                found.Length, found.Count(l => l.Tls), failures.Count, started.ElapsedMilliseconds);
            Post(() =>
            {
                if (Preferences.InterfaceId != lan.InterfaceId)
                {
                    DiscoveryDetail = "LAN selection changed; discarded discovery from the previous interface.";
                    _log.LogInformation("Discovery result discarded because LAN selection changed");
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
            _log.LogWarning(ex, "Light discovery failed; elapsedMs={ElapsedMs}", started.ElapsedMilliseconds);
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
        _log.LogInformation("Adding a light by address; endpoint={Endpoint}", Token(address));
        var transport = new ElgatoTransport();
        var info = await transport.IdentifyDetailsAsync(device, _stop.Token);
        await transport.ReadAsync(device, _stop.Token);
        _log.LogInformation("Manual light address validated; endpoint={Endpoint}", Token(address));
        Post(() => Change(s => { s.Lights.RemoveAll(l => l.Id == device.Id); s.Lights.Add(device with {
            Name = info.DisplayName ?? info.ProductName ?? device.Name, ProductName = info.ProductName }); }));
    }
    internal async Task TestLightAsync(LightDevice device)
    {
        _log.LogInformation("Read-only connection test requested {Light}", device.Name);
        if (Demo) { Notice = "Simulated light reachable."; Notify(); return; }
        var state = await ReadLightAsync(device, _stop.Token);
        _log.LogInformation("Read-only connection test succeeded {Light}", device.Name);
        Post(() => { Notice = $"{device.Name}: reachable, {(state.On == 1 ? "on" : "off")}, brightness {state.Brightness}%. Read-only test."; Notify(); });
    }
    private async Task<LightState> ReadLightAsync(LightDevice device, CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            var state = await _transport.ReadAsync(device, token);
            RememberLight(device, state, started);
            Post(Notify);
            return state;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or
            OperationCanceledException && !token.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (!_observations.TryGetValue(device.Id, out var latest) || latest.Time <= started)
                {
                    _lightReadings[device.Id] = "Readback failed: " + ex.Message;
                    _observations[device.Id] = Observation(device.Id) with { Error = ex.Message };
                }
            }
            Post(Notify);
            throw;
        }
    }
    private void RememberLight(LightDevice device, LightState state, DateTimeOffset? time = null)
    {
        lock (_gate)
        {
            var observedAt = time ?? DateTimeOffset.UtcNow;
            if (_observations.TryGetValue(device.Id, out var previous) && previous.Time > observedAt) return;
            _observations[device.Id] = new(state, observedAt);
            _lightReadings[device.Id] = $"{(state.On == 1 ? "ON" : "OFF")} - brightness {state.Brightness}% - temperature {state.Temperature} (device readback)";
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
        _log.LogInformation("Explicit elevated helper setup requested");
        await Setup.InstallAsync();
        _log.LogInformation("Helper setup completed");
        Post(() =>
        {
            Notice = "Legacy helper setup completed. It is not the active Windows sensor source; setup success alone does not validate ETW or IPC health.";
            Notify();
        });
    }
    internal void Resume()
    {
        _log.LogInformation("System resume notified; invalidating sensor observation");
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
        _log.LogInformation("Session stopping; ownedLights={OwnedLights}", _automation.Owned.Count);
        UI.AppTheme.Stop();
        var failures = new List<Exception>();
        _stop.Cancel();
        try { await StopPreviewAsync(); }
        catch (Exception ex) { failures.Add(ex); }
        try { await Task.WhenAll(_loop ?? Task.CompletedTask, _monitor ?? Task.CompletedTask,
            _appDetails ?? Task.CompletedTask, _lightMaintenance ?? Task.CompletedTask, Task.WhenAll(_editing.ToArray())); }
        catch (Exception ex) { failures.Add(ex); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var operation = _log.BeginScope("operation=shutdown-restore");
            await _automation.ReconcileAsync(false, Preferences.Lights, timeout.Token);
        }
        catch (Exception ex) { failures.Add(ex); }
        if (failures.Count > 0)
        {
            var failure = new AggregateException("Camera/light cleanup did not complete successfully.", failures);
            _log.LogError(failure, "Session cleanup incomplete; ownedLights={OwnedLights}", _automation.Owned.Count);
            throw failure;
        }
        _log.LogInformation("Session stopped; owned lights restored");
    }
    private sealed class DemoLights : ILightTransport
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LightState> _states = new();
        public Task<LightState> ReadAsync(LightDevice device, CancellationToken cancellationToken) =>
            Task.FromResult(_states.GetOrAdd(device.Id, new LightState(0, 26, 205)));
        public Task WriteAsync(LightDevice device, LightState state, CancellationToken cancellationToken)
        { _states[device.Id] = state; return Task.CompletedTask; }
    }
}
