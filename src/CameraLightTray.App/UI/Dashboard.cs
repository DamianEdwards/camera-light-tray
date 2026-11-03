using CameraLightTray.Core;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

namespace CameraLightTray.App.UI;

internal sealed class Dashboard(AppSession session) : Component
{
    private static Element Copy(string text) => TextBlock(text).TextWrapping().FontSize(13);
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
            a.Key.Contains(session.AppSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        return ScrollView(VStack(20,
            VStack(6,
                TextBlock("Camera Light Tray").FontSize(28).SemiBold(),
                Copy("Camera-triggered Elgato lighting - initial spike"),
                session.Isolated ? InfoBar("Isolated run", "Separate preferences; setup and login startup are disabled. Automation starts off.")
                    .Informational().IsClosable(false) : null,
                session.Demo ? InfoBar("Synthetic demo", "All camera and light state is simulated. No hardware writes or machine setup.")
                    .Success().IsClosable(false) : null),
            !string.IsNullOrEmpty(session.Error) ? InfoBar("Action failed", session.Error).Error().IsClosable(false) : null,
            !string.IsNullOrEmpty(session.Notice) ? Copy(session.Notice).AutomationId("Notice") : null,
            Card(VStack(12,
                TextBlock("Automation").FontSize(20).SemiBold(),
                ToggleSwitch(preferences.AutomationEnabled, session.Enable)
                    .AutomationName("Enable physical light automation").AutomationId("AutomationEnabled"),
                Copy(preferences.AutomationEnabled ? "Enabled. Only selected apps and lights participate." :
                    "Off. Enabling this permits changes to selected physical lights."),
                ComboBox(["Automatic", "Paused (restore owned lights)", "Manual on", "Manual off (restore previous state)"],
                    (int)session.Mode, i => session.SetMode((ManualMode)i))
                    .AutomationName("Automation override").AutomationId("Override"),
                Copy(session.AutomationDetail).AutomationId("AutomationStatus"),
                Copy("An external change relinquishes ownership until the camera activation ends. Manual off releases our changes; it does not turn off lights that were already on."))),
            Card(VStack(12,
                TextBlock("Camera monitor").FontSize(20).SemiBold(),
                Copy(session.SensorDetail).AutomationId("SensorStatus"),
                Copy("Uses Media Foundation capture-engine lifecycle events, not camera frames. Unknown is never treated as proof the camera is off. Other camera APIs and captures already active at helper startup may remain unknown."),
                Button("Set up / repair monitor (administrator)", () => session.Run(session.SetupAsync))
                    .AutomationId("SetupMonitor").IsEnabled(!session.Busy && !session.Isolated && !session.Demo),
                Copy("One-time setup copies the helper into protected Program Files and registers a LocalService service. Normal startup and app selection never request elevation."))),
            Card(VStack(12,
                TextBlock("Apps").FontSize(20).SemiBold(),
                Copy("Select any number of apps. Lights remain on while any selected app is active. Running apps appear automatically; saved selections remain listed."),
                TextBox(session.AppSearch, value => { session.AppSearch = value; session.Notify(); }, "Filter apps")
                    .AutomationName("Filter apps").AutomationId("AppSearch"),
                VStack(8, apps.Select(app =>
                    (Element)HStack(12,
                        session.IconPath(app.Key) is { } icon ? Image(icon).Width(32).Height(32).AccessibilityHidden() :
                            Icon(FontIcon("\uE71D", "Segoe Fluent Icons", 22)).Width(32).Height(32),
                        VStack(3,
                            CheckBox(preferences.Apps.Any(a => a.Enabled && a.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase)),
                                value => session.SelectApp(app, value == true), app.Name)
                                .AutomationName("Monitor " + app.Name),
                            Copy($"{app.Status} - {app.Key}").FontSize(11)))).ToArray()),
                apps.Length == 0 ? Copy("No matching running apps. Launch an app or add an executable.") : null,
                TextBox(session.AppPath, value => session.AppPath = value, "Full executable path")
                    .AutomationName("Executable path").AutomationId("AppPath"),
                Button("Add executable", session.AddApp).AutomationId("AddApp"),
                Copy("Packaged apps are identified by package family + executable (stable across package updates); desktop apps by full executable path."))),
            Card(VStack(12,
                TextBlock("LAN and lights").FontSize(20).SemiBold(),
                Copy("Choose the physical LAN interface. Discovery and HTTP requests bind to it explicitly; no VPN route changes are made."),
                ComboBox(interfaces.Select(i => $"{i.Name} - {i.Address}").ToArray(),
                    Array.FindIndex(interfaces, i => i.InterfaceId == preferences.InterfaceId),
                    index => { if (index >= 0 && index < interfaces.Length) session.SelectInterface(interfaces[index].InterfaceId); })
                    .AutomationName("LAN interface").AutomationId("LanInterface").IsEnabled(!session.Demo),
                HStack(8, Button("Refresh interfaces", session.RefreshInterfaces).IsEnabled(!session.Demo),
                    Button("Discover lights", () => session.Run(session.DiscoverAsync)).IsEnabled(!session.Busy && !session.Demo)),
                VStack(10, preferences.Lights.Select(light => (Element)VStack(4,
                    CheckBox(light.Enabled, value => session.SelectLight(light, value == true), light.Name)
                        .IsEnabled(!light.Tls).AutomationName("Automate " + light.Name),
                    Copy(light.Tls ? "Unsupported TLS model; no requests will be made." :
                        $"{light.Host}:{light.Port} - bound to {light.InterfaceId}").FontSize(11),
                    Button("Test connection (read only)", () => session.Run(() => session.TestLightAsync(light)))
                        .IsEnabled(!session.Busy && !light.Tls))).ToArray()),
                TextBox(session.LightAddress, value => session.LightAddress = value, "Manual light IPv4 address (port 9123)")
                    .AutomationName("Light IPv4 address").IsEnabled(!session.Demo),
                Button("Add light (read-only probe)", () => session.Run(session.AddLightAsync)).IsEnabled(!session.Busy && !session.Demo),
                Copy("Classic Key Light / Key Light Air HTTP models only. New TLS models are reported as unsupported. New lights are disabled until selected."))),
            Card(VStack(12,
                TextBlock("Lighting rules").FontSize(20).SemiBold(),
                Copy("Activation debounce (milliseconds, 100-10000)"),
                TextBox(session.ActivationInput, value => session.ActivationInput = value).AutomationName("Activation debounce").AutomationId("ActivationDelay"),
                Copy("Leave grace (milliseconds, 500-60000)"),
                TextBox(session.LeaveInput, value => session.LeaveInput = value).AutomationName("Leave grace").AutomationId("LeaveDelay"),
                Button("Save rules", session.SaveRules).AutomationId("SaveRules"),
                ToggleSwitch(preferences.StartAtLogin, session.SetStartup).AutomationName("Start at login")
                    .IsEnabled(!session.Isolated && !session.Demo),
                Copy("Closing this window hides it in the tray. Right-click the tray icon to pause or exit."))),
            session.Demo ? Card(VStack(8,
                TextBlock("Synthetic camera controls").SemiBold(),
                HStack(8, Button("Start demo camera", () => session.DemoCapture(true)).AutomationId("DemoStart"),
                    Button("Stop demo camera", () => session.DemoCapture(false)).AutomationId("DemoStop")))) : null,
            Copy("No telemetry, camera images, audio or ETL recording. Preferences and recovery state stay in your local data directory.")
        ).Padding(24)).AutomationId("Dashboard");
    }
}
