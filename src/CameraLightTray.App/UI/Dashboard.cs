using CameraLightTray.Core;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

namespace CameraLightTray.App.UI;

internal sealed class Dashboard(AppSession session) : Component
{
    private static Element Copy(string text) => TextBlock(text).TextWrapping().FontSize(13).AutomationName(text);
    private static Element Scroll(Element child) => ScrollViewer(child).Set(element =>
    {
        var viewer = (Microsoft.UI.Xaml.Controls.ScrollViewer)element;
        viewer.HorizontalScrollMode = Microsoft.UI.Xaml.Controls.ScrollMode.Disabled;
        viewer.HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled;
        viewer.VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto;
    });
    public override Element Render()
    {
        var (_, setRevision) = UseState(session.Revision);
        UseEffect(() =>
        {
            void Changed() => setRevision(session.Revision);
            session.Changed += Changed;
            return () => session.Changed -= Changed;
        }, session);
        var preferences = session.Preferences;
        var interfaces = session.Interfaces;
        var apps = session.Apps.Where(a => string.IsNullOrWhiteSpace(session.AppSearch) ||
            a.Name.Contains(session.AppSearch, StringComparison.OrdinalIgnoreCase) ||
            a.Key.Contains(session.AppSearch, StringComparison.OrdinalIgnoreCase))
            .Where(a => session.ShowAllApps || !string.IsNullOrWhiteSpace(session.AppSearch) || session.Suggested(a)).ToArray();
        var cameras = session.Cameras;
        var header = VStack(6,
                TextBlock("Camera Light Tray").FontSize(28).SemiBold(),
                Copy("Camera-triggered Elgato lighting - initial spike"),
                session.Isolated ? InfoBar("Isolated run", "Separate preferences; setup and login startup are disabled. Automation starts off.")
                    .Informational().IsClosable(false) : null,
                session.Demo ? InfoBar("Synthetic demo", "All camera and light state is simulated. No hardware writes or machine setup.")
                    .Success().IsClosable(false) : null);
        var automation = Card(VStack(10,
                TextBlock("Automation").FontSize(20).SemiBold(),
                ToggleSwitch(preferences.AutomationEnabled, session.Enable)
                    .AutomationName("Enable physical light automation").AutomationId("AutomationEnabled"),
                Copy(preferences.AutomationEnabled ? "Enabled. Only selected apps and lights participate." :
                    "Off. Enabling this permits changes to selected physical lights."),
                ComboBox(["Automatic", "Paused (restore owned lights)", "Manual on", "Manual off (restore previous state)"],
                    (int)session.Mode, i => session.SetMode((ManualMode)i))
                    .AutomationName("Automation override").AutomationId("Override"),
                Copy(session.AutomationDetail).AutomationId("AutomationStatus"),
                Copy("Selected apps: " + string.Join("; ", session.Apps.Where(a => preferences.Apps.Any(p =>
                    p.Enabled && p.Key.Equals(a.Key, StringComparison.OrdinalIgnoreCase))).Select(a => $"{a.Name}: {a.Status}")))));
        var cameraSettings = Card(VStack(12,
                TextBlock("Camera monitor").FontSize(20).SemiBold(),
                Copy(session.SensorDetail).AutomationId("SensorStatus"),
                Copy("Windows sensor activity reports identify streaming clients and cameras without elevation. Monitoring does not open a camera. Unknown holds automation; it is not treated as off."),
                Copy(preferences.CameraIds.Count == 0 ? "Trigger cameras: all." : $"Trigger cameras: {preferences.CameraIds.Count} selected. Uncheck the last selection to monitor all."),
                VStack(4, cameras.Select(camera => (Element)CheckBox(
                    preferences.CameraIds.Contains(camera.Id, StringComparer.OrdinalIgnoreCase),
                    enabled => session.SelectCamera(camera, enabled == true), camera.Name)
                    .AutomationName("Trigger on " + camera.Name)).ToArray()),
                preferences.CameraIds.Any(id => !cameras.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) ?
                    Copy("A saved trigger camera is unavailable. Select 'Monitor all cameras' to clear the filter.") : null,
                Button("Monitor all cameras", session.MonitorAllCameras).IsEnabled(preferences.CameraIds.Count > 0),
                Copy("App selections and camera filters are independent. The preview selector alone does not change trigger cameras.")));
        var preview = !session.Demo ? Card(VStack(8,
                TextBlock("Local camera preview").FontSize(20).SemiBold(),
                ComboBox(cameras.Select(c => c.Name).ToArray(), Array.FindIndex(cameras, c => c.Id == session.PreviewCameraId),
                    index => { if (index >= 0 && index < cameras.Length) { session.PreviewCameraId = cameras[index].Id; session.Notify(); } })
                    .AutomationName("Preview camera").IsEnabled(!session.Busy),
                HStack(8, Button("Start preview", () => session.Run(session.StartPreviewAsync)).IsEnabled(!session.Busy && cameras.Length > 0),
                    Button("Stop preview", () => session.Run(session.StopPreviewAsync)).IsEnabled(!session.Busy)),
                MediaPlayerElement(null).Height(220).OnMount(element => session.AttachPreview((Microsoft.UI.Xaml.Controls.MediaPlayerElement)element))
                    .OnUnmount(_ => session.HidePreview()),
                Copy(session.PreviewDetail).AutomationId("PreviewStatus"),
                Copy("Automation: " + session.AutomationDetail),
                VStack(4, preferences.Lights.Where(l => l.Enabled).Select(l => (Element)Copy(l.Name + ": " + session.LightReading(l.Id))).ToArray())
            )) : null;
        var appSettings = Card(VStack(12,
                TextBlock("Apps").FontSize(20).SemiBold(),
                Copy("Select any number of apps. Lights remain on while any selected app is active. Running apps appear automatically; saved selections remain listed."),
                TextBox(session.AppSearch, value => { session.AppSearch = value; session.Notify(); }, "Filter apps")
                    .AutomationName("Filter apps").AutomationId("AppSearch"),
                ToggleSwitch(session.ShowAllApps, value => { session.ShowAllApps = value; session.Notify(); })
                    .AutomationName("Show all running apps"),
                Copy("Suggested first: saved selections, active cameras, installed packages declaring webcam access, and known communications apps. Suggestions do not grant permission or select apps automatically."),
                Scroll(VStack(8, apps.Select(app =>
                    (Element)HStack(12,
                        session.IconPath(app.Key) is { } icon ? Image(icon).Width(32).Height(32).AccessibilityHidden() :
                            Icon(FontIcon("\uE71D", "Segoe Fluent Icons", 22)).Width(32).Height(32),
                        VStack(3,
                            CheckBox(preferences.Apps.Any(a => a.Enabled && a.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase)),
                                value => session.SelectApp(app, value == true), app.Name)
                                .AutomationName("Monitor " + app.Name),
                            Copy($"{app.Status} - {app.Key}").FontSize(11)))).ToArray())).Height(280),
                apps.Length == 0 ? Copy("No matching running apps. Launch an app or add an executable.") : null,
                TextBox(session.AppPath, value => session.AppPath = value, "Full executable path")
                    .AutomationName("Executable path").AutomationId("AppPath"),
                Button("Add executable", session.AddApp).AutomationId("AddApp"),
                Copy("Packaged apps are identified by package family + executable (stable across package updates); desktop apps by full executable path.")));
        var lightSettings = Card(VStack(12,
                TextBlock("LAN and lights").FontSize(20).SemiBold(),
                Copy("Choose the physical LAN interface. Discovery and HTTP requests bind to it explicitly; no VPN route changes are made."),
                ComboBox(interfaces.Select(i => $"{i.Name} - {i.Address}").ToArray(),
                    Array.FindIndex(interfaces, i => i.InterfaceId == preferences.InterfaceId),
                    index => { if (index >= 0 && index < interfaces.Length) session.SelectInterface(interfaces[index].InterfaceId); })
                    .AutomationName("LAN interface").AutomationId("LanInterface").IsEnabled(!session.Demo),
                HStack(8, Button("Refresh interfaces", session.RefreshInterfaces).IsEnabled(!session.Demo),
                    Button("Discover lights", () => session.Run(session.DiscoverAsync)).IsEnabled(!session.Busy && !session.Demo)),
                !string.IsNullOrEmpty(session.DiscoveryDetail) ?
                    Copy(session.DiscoveryDetail).AutomationId("DiscoveryStatus") : null,
                VStack(10, preferences.Lights.Select(light => (Element)VStack(4,
                    CheckBox(light.Enabled, value => session.SelectLight(light, value == true), light.Name)
                        .IsEnabled(!light.Tls).AutomationName("Automate " + light.Name),
                    Copy(light.Tls ? "Unsupported TLS model; no requests will be made." :
                        $"{light.Host}:{light.Port} - bound to {light.InterfaceId}").FontSize(11),
                    Copy(session.LightReading(light.Id)),
                    Button("Test connection (read only)", () => session.Run(() => session.TestLightAsync(light)))
                        .IsEnabled(!session.Busy && !light.Tls))).ToArray()),
                TextBox(session.LightAddress, value => session.LightAddress = value, "Manual light IPv4 address (port 9123)")
                    .AutomationName("Light IPv4 address").IsEnabled(!session.Demo),
                Button("Add light (read-only probe)", () => session.Run(session.AddLightAsync)).IsEnabled(!session.Busy && !session.Demo),
                Copy("Classic Key Light / Key Light Air HTTP models only. New TLS models are reported as unsupported. New lights are disabled until selected.")));
        var rules = Card(VStack(12,
                TextBlock("Lighting rules").FontSize(20).SemiBold(),
                Copy("Activation debounce (milliseconds, 100-10000)"),
                TextBox(session.ActivationInput, value => session.ActivationInput = value).AutomationName("Activation debounce").AutomationId("ActivationDelay"),
                Copy("Leave grace (milliseconds, 500-60000)"),
                TextBox(session.LeaveInput, value => session.LeaveInput = value).AutomationName("Leave grace").AutomationId("LeaveDelay"),
                Button("Save rules", session.SaveRules).AutomationId("SaveRules"),
                ToggleSwitch(preferences.StartAtLogin, session.SetStartup).AutomationName("Start at login")
                    .IsEnabled(!session.Isolated && !session.Demo),
                Copy("Closing this window hides it in the tray and stops preview. Right-click the tray icon to pause or exit."),
                Copy("External light changes relinquish ownership until the activation ends. Manual off restores only our changes, never lights that were already on.")));
        var legacy = Card(VStack(8,
                TextBlock("Legacy ETW helper (optional)").SemiBold(),
                Copy("The normal-user Windows sensor monitor above does not need this service. The experimental capture-engine ETW helper is retained for diagnostic/integration work; it does not cover every camera API and is not the active UI source."),
                Button("Set up / repair legacy helper (administrator)", () => session.Run(session.SetupAsync))
                    .AutomationId("SetupMonitor").IsEnabled(!session.Busy && !session.Isolated && !session.Demo),
                Copy("Explicit setup installs protected files and a LocalService service. It never runs automatically at login.")));
        var demoControls = session.Demo ? Card(VStack(8,
                TextBlock("Synthetic camera controls").SemiBold(),
                HStack(8, Button("Start demo camera", () => session.DemoCapture(true)).AutomationId("DemoStart"),
                    Button("Stop demo camera", () => session.DemoCapture(false)).AutomationId("DemoStop")))) : null;
        void Navigate(int page) { session.Page = page; session.Notify(); }
        return Scroll(VStack(12,
            header,
            HStack(6, Button("Overview", () => Navigate(0)).IsEnabled(session.Page != 0).AutomationId("OpenOverview"),
                Button("Apps", () => Navigate(1)).IsEnabled(session.Page != 1),
                Button("Cameras", () => Navigate(2)).IsEnabled(session.Page != 2),
                Button("Lights", () => Navigate(3)).IsEnabled(session.Page != 3),
                Button("Rules / helper", () => Navigate(4)).IsEnabled(session.Page != 4).AutomationId("OpenRules")),
            VStack(4,
                !string.IsNullOrEmpty(session.Error) ? InfoBar("Action failed", session.Error).Error().IsClosable(false) : null,
                !string.IsNullOrEmpty(session.Notice) ? Copy(session.Notice).AutomationId("Notice") : null),
            session.Page switch
            {
                1 => appSettings,
                2 => cameraSettings,
                3 => lightSettings,
                4 => VStack(12, rules, legacy),
                _ => VStack(12, automation, Copy(session.SensorDetail).AutomationId("OverviewSensorStatus"), preview, demoControls)
            },
            Copy("No telemetry, audio, image or ETL recording. Explicit preview stays local and stops when the window closes; background monitoring uses metadata only.")
        ).Padding(24)).AutomationId("Dashboard");
    }
}
