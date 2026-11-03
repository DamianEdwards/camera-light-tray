using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage;
using Windows.Storage.FileProperties;
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
    private Task? _loop, _monitor;
    private CameraApp[] _catalog = [];
    private readonly Dictionary<string, string> _icons = new(StringComparer.OrdinalIgnoreCase);
    internal event Action? Changed;
    internal int Revision { get; private set; }
    internal bool Isolated { get; }
    internal bool Demo { get; }
    internal string Error { get; private set; } = "";
    internal string SensorDetail { get; private set; } = "Monitor not connected; state is unknown.";
    internal string AutomationDetail { get; private set; } = "Automation is off.";
    internal string Notice { get; private set; } = "";
    internal bool Busy { get; private set; }
    internal ManualMode Mode { get; private set; }
    internal string AppPath { get; set; } = "";
    internal string LightAddress { get; set; } = "";
    internal string AppSearch { get; set; } = "";
    internal string ActivationInput { get; set; } = "";
    internal string LeaveInput { get; set; } = "";
    internal LanAddress[] Interfaces { get; private set; } = [];
    internal string? IconPath(string appKey) { lock (_gate) return _icons.GetValueOrDefault(appKey); }
    internal Settings Preferences { get { lock (_gate) return _settings with { Apps = [.. _settings.Apps], Lights = [.. _settings.Lights] }; } }
    internal CameraApp[] Apps { get { lock (_gate) return _catalog.UnionBy(_snapshot?.Apps ?? [], a => a.Key, StringComparer.OrdinalIgnoreCase)
        .UnionBy(_settings.Apps.Select(a => new CameraApp(a.Key, a.Name, CameraStatus.Inactive)), a => a.Key,
            StringComparer.OrdinalIgnoreCase)
        .Select(a => a with { Status = _snapshot?.Apps.FirstOrDefault(p => p.Key.Equals(a.Key, StringComparison.OrdinalIgnoreCase))?.Status ??
            (_snapshot?.Healthy == true ? CameraStatus.Inactive : CameraStatus.Unknown) })
        .OrderBy(a => a.Name).ToArray(); } }

    internal AppSession(Storage storage, bool isolated, bool demo)
    {
        _storage = storage; Isolated = isolated; Demo = demo;
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
            _monitor = MonitorClient.RunAsync(snapshot => { lock (_gate) _snapshot = snapshot; },
                detail => Post(() => { SensorDetail = detail; Notify(); }), _stop.Token);
        _loop = RunAsync(_stop.Token);
    }
    private async Task RunAsync(CancellationToken token)
    {
        int ticks = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            do
            {
                if (!Demo && ticks++ % 20 == 0)
                {
                    var list = Catalog();
                    lock (_gate) _catalog = list;
                    await CacheIconsAsync(list, token);
                }
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
                    Post(() => { AutomationDetail = _automation.Status; Notify(); });
                }
                catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or
                    OperationCanceledException or NotSupportedException or UnauthorizedAccessException)
                { Post(() => Report("Light operation failed: " + ex.Message)); }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Post(() => Report("Automation worker stopped: " + ex.Message)); }
    }
    private static CameraApp[] Catalog()
    {
        using var user = WindowsIdentity.GetCurrent();
        using var current = Process.GetCurrentProcess();
        int sessionId = current.SessionId;
        var list = new List<CameraApp>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != sessionId || process.MainWindowHandle == 0) continue;
                    var identity = CameraLightTray.Monitor.ProcessCatalog.Read(process.Id);
                    if (identity?.UserSid == user.User?.Value)
                        list.Add(new(identity!.AppKey, identity.Name, CameraStatus.Unknown));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
            }
        }
        return list.DistinctBy(a => a.Key, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private async Task CacheIconsAsync(CameraApp[] apps, CancellationToken token)
    {
        foreach (var app in apps)
        {
            if (!Path.IsPathFullyQualified(app.Key) || IconPath(app.Key) is not null) continue;
            try
            {
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
    internal void SelectInterface(string id) => Change(s => s.InterfaceId = id);
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
        RefreshInterfaces();
        var lan = Interfaces.FirstOrDefault(i => i.InterfaceId == Preferences.InterfaceId)
            ?? throw new InvalidOperationException("Choose a LAN interface first.");
        var found = await MdnsDiscovery.DiscoverAsync(lan, _stop.Token);
        var transport = new ElgatoTransport();
        for (int i = 0; i < found.Length; i++)
            if (!found[i].Tls) found[i] = found[i] with { Name = await transport.IdentifyAsync(found[i], _stop.Token) };
        Post(() =>
        {
            Change(s =>
            {
                foreach (var light in found)
                {
                    var old = s.Lights.FirstOrDefault(l => l.Id == light.Id);
                    s.Lights.RemoveAll(l => l.Id == light.Id);
                    s.Lights.Add(light with { Enabled = old?.Enabled ?? false });
                }
            });
            Notice = $"Discovered {found.Length} light(s); new lights are not enabled automatically."; Notify();
        });
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
        var state = await new ElgatoTransport().ReadAsync(device, _stop.Token);
        Post(() => { Notice = $"{device.Name}: reachable, {(state.On == 1 ? "on" : "off")}, brightness {state.Brightness}%. Read-only test."; Notify(); });
    }
    internal void SetStartup(bool enabled)
    {
        if (Isolated || Demo) { Report("Login startup is disabled for isolated/demo runs."); return; }
        try { Setup.Startup(enabled); Change(s => s.StartAtLogin = enabled); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Report(ex.Message); }
    }
    internal Task SetupAsync() => Isolated || Demo ?
        Task.FromException(new InvalidOperationException("Setup is disabled for isolated/demo runs.")) : Setup.InstallAsync();
    internal void Resume() { lock (_gate) _snapshot = null; SensorDetail = "System resumed; waiting for fresh monitor state."; Notify(); }
    internal void DemoCapture(bool active)
    {
        lock (_gate) _snapshot = new(1, Revision + 1, 1, DateTimeOffset.UtcNow, true, "Synthetic capture.",
            [new("demo:meeting", "Example Meeting", active ? CameraStatus.Active : CameraStatus.Inactive)]);
        Notify();
    }
    internal async Task StopAsync()
    {
        _stop.Cancel();
        if (_loop is not null) await _loop;
        if (_monitor is not null) await _monitor;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await _automation.ReconcileAsync(false, Preferences.Lights, timeout.Token);
    }
    private sealed class DemoLights : ILightTransport
    {
        private LightState _state = new(0, 40, 250);
        public Task<LightState> ReadAsync(LightDevice device, CancellationToken cancellationToken) => Task.FromResult(_state);
        public Task WriteAsync(LightDevice device, LightState state, CancellationToken cancellationToken)
        { _state = state; return Task.CompletedTask; }
    }
}
