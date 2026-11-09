using CameraLightTray.Core;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

namespace CameraLightTray.App.UI;

// Studio: the equipment is the daily control surface; setup lives in one quiet
// navigation rail. Power and automation remain separate, including uncertainty.
internal sealed class Dashboard(AppSession session) : Component
{
    public override Element Render()
    {
        var (_, update) = UseState(session.ShellRevision);
        UseEffect(() =>
        {
            void Changed() => update(session.ShellRevision);
            session.ShellChanged += Changed;
            return () => session.ShellChanged -= Changed;
        }, session);
        string[] names = ["Automation", "Apps", "Cameras", "Lights", "General", "Diagnostics"];
        string[] glyphs = ["\uE80F", "\uE71D", "\uE714", "\uE781", "\uE713", "\uE9D9"];
        var navigation = UseMemo(() =>
        {
            session.SettingsShellRenders++;
            return VStack(6, names.Select((name, index) => (Element)Button(
                HStack(12, StudioStyle.Glyph(glyphs[index]).VAlign(VerticalAlignment.Center),
                    TextBlock(name).FontSize(13).VAlign(VerticalAlignment.Center)),
                () => session.Page = index)
                .HorizontalContentAlignment(HorizontalAlignment.Left).HAlign(HorizontalAlignment.Stretch).Padding(12, 10)
                .Background(StudioStyle.Color(session.Page == index ? "#DFE9F1" : "Transparent", true)).BorderThickness(0)
                .AutomationName(name).WithKey(name).AutomationId(index == 0 ? "OpenRules" : index == 4 ? "OpenGeneral" : "Open" + name)).ToArray())
                .Padding(12, 24).Background(StudioStyle.Color("#F0F3F6", true));
        }, session.Page, session.ShellRevision);
        return Grid([GridSize.Px(180), GridSize.Star()], [GridSize.Star()], [
            navigation.Grid(),
            Component<DashboardPage, DashboardPageProps>(new(session, session.Page, session.ShellRevision)).Grid(column: 1)
        ]).Background(StudioStyle.Color("#FBFCFD", true)).RequestedTheme(AppTheme.Element).AutomationId("Dashboard");
    }
}

internal sealed record DashboardPageProps(AppSession Session, int Page, int ShellRevision);
internal sealed class DashboardPage : Component<DashboardPageProps>
{
    private AppSession session => Props.Session;
    private static TextBlockElement Copy(string text, double size = 13) =>
        TextBlock(text).FontSize(size).TextWrapping().AutomationName(text);
    private static Element Description(string text) => Copy(text).Opacity(0.8);
    private static Element Rule() => Border(null).Height(1).Background(StudioStyle.Color("#D8E0E6"));
    private static Element Heading(string title, string description) =>
        VStack(8, Copy(title, 26).SemiBold(), Description(description)).Margin(bottom: 24);
    private static Element Section(string title, Element content) =>
        VStack(10, Copy(title, 16).SemiBold(), content).Margin(top: 20).HAlign(HorizontalAlignment.Stretch);
    private static Element Row(Element content, Element action) => VStack(0,
        StudioStyle.Split(content, action).Padding(0, 16), Rule());
    private static CheckBoxElement Selection(bool selected, Action<bool> changed, string name) =>
        CheckBox(selected, changed, "").MinWidth(44).Width(44).MinHeight(44).Height(44).Padding(12, 0, 0, 0).AutomationName(name);
    private void Navigate(int page) { session.Page = page; session.Notify(); }

    public override Element Render()
    {
        var (_, update) = UseState(session.Revision);
        UseEffect(() =>
        {
            void Changed() => update(session.Revision);
            session.Changed += Changed;
            return () => session.Changed -= Changed;
        }, session);
        return ScrollViewer(VStack(12,
                session.Demo ? InfoBar("Synthetic demo", "Example camera and lights only; no hardware access.").Informational().IsClosable(false) :
                    session.Isolated ? InfoBar("Isolated run", "Separate preferences; machine setup and startup are disabled.").Informational().IsClosable(false) : null,
                !string.IsNullOrEmpty(session.Error) ? InfoBar("Action failed", session.Error).Error().IsClosable(true).Closed(() => session.Report("")) : null,
                !string.IsNullOrEmpty(session.Notice) ? Description(session.Notice) : null,
                Props.Page switch { 1 => Apps(), 2 => Cameras(), 3 => Lights(), 4 => General(), 5 => Diagnostics(), _ => Automation() }
            ).Padding(32, 28)).HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .Set(viewer =>
            {
                viewer.HorizontalScrollMode = Microsoft.UI.Xaml.Controls.ScrollMode.Disabled;
                viewer.HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled;
                viewer.VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Visible;
            }).AutomationId("SettingsScroll");
    }

    private Element Apps()
    {
        var settings = session.Preferences;
        var apps = session.Apps.Where(a => string.IsNullOrWhiteSpace(session.AppSearch) ||
            a.Name.Contains(session.AppSearch, StringComparison.OrdinalIgnoreCase) ||
            a.Key.Contains(session.AppSearch, StringComparison.OrdinalIgnoreCase))
            .Where(a => session.ShowAllApps || !string.IsNullOrWhiteSpace(session.AppSearch) || session.Suggested(a)).ToArray();
        int selected = settings.Apps.Count(a => a.Enabled);
        return VStack(12,
            Heading("Apps that trigger your lights", "Only selected apps can turn on your lights. They stay on while any selected app uses a monitored camera."),
            Border(VStack(4, Copy(selected == 0 ? "Select an app to start" : $"{selected} {(selected == 1 ? "app" : "apps")} selected").SemiBold(),
                Description(selected == 0 ? "No selection means no automatic activation—not every app." :
                    "Other apps can use the camera without triggering lighting."))).Padding(14).Background(StudioStyle.Color("#EDF5FA", true)).CornerRadius(6),
            TextBox(session.AppSearch, value => { session.AppSearch = value; session.Notify(); }, "Search apps")
                .AutomationName("Search apps").AutomationId("AppSearch"),
            Rule(),
            VStack(0, apps.Select(app => Row(
                HStack(12, session.IconPath(app.Key) is { } path ? Image(path).Width(30).Height(30).VAlign(VerticalAlignment.Center).AccessibilityHidden() :
                    StudioStyle.Glyph("\uE71D", 24).Width(30).VAlign(VerticalAlignment.Center).AccessibilityHidden(),
                    VStack(4, Copy(app.Name).SemiBold(), Description(app.Status == CameraStatus.Active ? "Camera in use" :
                        app.Status == CameraStatus.Unknown ? "Camera state unknown" : "Not using a monitored camera"))),
                Selection(settings.Apps.Any(a => a.Enabled && a.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase)),
                    value => session.SelectApp(app, value), "Trigger lights for " + app.Name)).WithKey(app.Key)).ToArray()),
            apps.Length == 0 ? Description("No matching apps. Try another search or add an executable below.") : null,
            StudioStyle.Split(Button(session.ShowAllApps ? "Show suggested apps" : "Show all running apps",
                () => { session.ShowAllApps = !session.ShowAllApps; session.Notify(); }).AutomationName("Show or hide all running apps").Background(StudioStyle.Color("Transparent", true)).BorderThickness(0),
                Button("Clear selection", session.ClearApps).Background(StudioStyle.Color("Transparent", true)).BorderThickness(0)),
            Section("Add an app by executable path", VStack(10,
                TextBox(session.AppPath, value => session.AppPath = value, "Full path to an .exe").AutomationName("Executable path").AutomationId("AppPath"),
                Button("Add executable", session.AddApp).AutomationId("AddApp"))),
            Button("App identity details", () => Navigate(5)).Background(StudioStyle.Color("Transparent", true)).BorderThickness(0)
        );
    }

    private Element Cameras()
    {
        var settings = session.Preferences;
        var cameras = session.Cameras;
        return VStack(12,
            Heading("Trigger cameras", "Lighting follows selected apps using these cameras. The preview camera is a separate choice."),
            Row(VStack(4, Copy("All cameras").SemiBold(), Description("Any camera used by a selected app")),
                Selection(settings.CameraIds.Count == 0, enabled =>
                {
                    if (enabled) session.MonitorAllCameras();
                    else if (cameras.Length > 0) session.SelectCamera(cameras[0], true);
                }, "Monitor all cameras")),
            VStack(0, cameras.Select(camera => Row(HStack(12, StudioStyle.Glyph("\uE714").VAlign(VerticalAlignment.Center).AutomationId("CameraGlyph-" + camera.Id),
                Copy(camera.Name).VAlign(VerticalAlignment.Center).AutomationId("CameraName-" + camera.Id)), Selection(settings.CameraIds.Count == 0 || settings.CameraIds.Contains(camera.Id, StringComparer.OrdinalIgnoreCase),
                    enabled => session.SelectCamera(camera, enabled), "Trigger on " + camera.Name).IsEnabled(settings.CameraIds.Count > 0)).WithKey(camera.Id)).ToArray()),
            settings.CameraIds.Any(id => !cameras.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) ?
                InfoBar("Saved camera unavailable", "Choose an available camera or monitor all cameras.").Warning().IsClosable(false) : null,
            !session.Demo ? Section("Local camera preview", VStack(12,
                Description("Select Camera Light Tray under Apps if you want this preview to trigger lighting. No recording."),
                ComboBox(cameras.Select(c => c.Name).ToArray(), Array.FindIndex(cameras, c => c.Id == session.PreviewCameraId),
                    index => { if (index >= 0 && index < cameras.Length) { session.PreviewCameraId = cameras[index].Id; session.Notify(); } })
                    .AutomationName("Preview camera"),
                HStack(8, Button("Start preview", () => session.Run(session.StartPreviewAsync)).IsEnabled(!session.Busy && cameras.Length > 0).AutomationId("StartPreview"),
                    Button("Stop preview", () => session.Run(session.StopPreviewAsync)).IsEnabled(!session.Busy).AutomationId("StopPreview")),
                MediaPlayerElement(null).Height(240).OnMount(element => session.AttachPreview((Microsoft.UI.Xaml.Controls.MediaPlayerElement)element))
                    .OnUnmount(_ => session.HidePreview()),
                Copy(session.PreviewDetail, 11).AutomationId("PreviewStatus"))) : null,
            Button("Monitor details", () => Navigate(5)).Background(StudioStyle.Color("Transparent", true)).BorderThickness(0)
        );
    }

    private Element Lights()
    {
        var settings = session.Preferences;
        var interfaces = session.Interfaces;
        return VStack(12,
            Heading("Your lights", "Choose which lights follow your camera. New lights are never enabled automatically."),
            Row(VStack(4, Copy("Connection").SemiBold(), Description("Discovery and control use this LAN interface.")),
                ComboBox(interfaces.Select(i => i.Name).ToArray(), Array.FindIndex(interfaces, i => i.InterfaceId == settings.InterfaceId),
                    index => { if (index >= 0 && index < interfaces.Length) session.SelectInterface(interfaces[index].InterfaceId); })
                    .Width(170).AutomationName("LAN interface").AutomationId("LanInterface").IsEnabled(!session.Demo)),
            HStack(8, Button("Discover lights", () => session.Run(session.DiscoverAsync)).IsEnabled(!session.Demo && !session.Busy),
                Button("Refresh interfaces", session.RefreshInterfaces).IsEnabled(!session.Demo)),
            !string.IsNullOrEmpty(session.DiscoveryDetail) ? Copy(session.DiscoveryDetail, 12).AutomationId("DiscoveryStatus") : null,
            settings.Lights.Count == 0 ? Description("No lights yet. Discover them on your LAN, then select the ones you want.") : null,
            VStack(0, settings.Lights.Select(light => Row(HStack(12, StudioStyle.Art(light, session.Observation(light.Id)),
                VStack(5, Copy(light.Name).SemiBold(), Description(light.Tls ? "TLS model · unsupported" :
                    session.Observation(light.Id).Fresh ? "Connected" : "Power unknown · reconnecting")).VAlign(VerticalAlignment.Center)),
                CheckBox(light.Enabled, value => session.SelectLight(light, value), "Auto").IsEnabled(!light.Tls)
                    .AutomationName("Automate " + light.Name)).WithKey(light.Id)).ToArray()),
            Button("Connection details", () => Navigate(5)).Background(StudioStyle.Color("Transparent", true)).BorderThickness(0),
            Section("Add a known address", VStack(10,
                TextBox(session.LightAddress, value => session.LightAddress = value, "Light IPv4 address").AutomationName("Light IPv4 address"),
                Button("Add light", () => session.Run(session.AddLightAsync)).IsEnabled(!session.Demo && !session.Busy)))
        );
    }

    private Element Automation()
    {
        var settings = session.Preferences;
        return VStack(12,
            Heading("Lighting, on cue", "Set up your triggers once. Then get back to your meeting."),
            Row(VStack(4, Copy(settings.Apps.Any(a => a.Enabled) ? $"{settings.Apps.Count(a => a.Enabled)} trigger {(settings.Apps.Count(a => a.Enabled) == 1 ? "app" : "apps")} selected" : "Choose trigger apps").SemiBold(),
                Description("Only selected apps can activate lights")), Button("Edit", () => Navigate(1))),
            Row(VStack(4, Copy(settings.CameraIds.Count == 0 ? "All cameras" : $"{settings.CameraIds.Count} trigger {(settings.CameraIds.Count == 1 ? "camera" : "cameras")}").SemiBold(),
                Description("Camera filters are independent of app selection")), Button("Edit", () => Navigate(2))),
            Row(VStack(4, Copy($"{settings.Lights.Count(l => l.Enabled && !l.Tls)} lights set to auto").SemiBold(),
                Description("Previous light states are preserved")), Button("Edit", () => Navigate(3))),
            Row(VStack(4, Copy("Enable automation").SemiBold(), Description("No selected apps means no automatic activation.")),
                ToggleSwitch(settings.AutomationEnabled, session.Enable).AutomationName("Enable automation").AutomationId("AutomationEnabled")),
            Row(VStack(4, Copy("Ignore brief camera checks").SemiBold(), Description("Delay before turning lights on, in milliseconds.")),
                TextBox(session.ActivationInput, value => session.ActivationInput = value).Width(100).AutomationName("Activation debounce").AutomationId("ActivationDelay")),
            Row(VStack(4, Copy("Keep lights on after capture stops").SemiBold(), Description("Leave grace, in milliseconds.")),
                TextBox(session.LeaveInput, value => session.LeaveInput = value).Width(100).AutomationName("Leave grace").AutomationId("LeaveDelay")),
            Button("Save timing", session.SaveRules).AutomationId("SaveRules"),
            Row(Copy("Override"), ComboBox(["Automatic", "Paused", "Manual on", "Manual off"], (int)session.Mode, index => session.SetMode((ManualMode)index)).AutomationName("Automation override")),
            Copy(session.ActivityTitle).SemiBold().AutomationId("AutomationStatus"),
            Button("Monitor and restoration details", () => Navigate(5)).Background(StudioStyle.Color("Transparent", true)).BorderThickness(0),
            session.Demo ? HStack(8, Button("Start demo camera", () => session.DemoCapture(true)).AutomationId("DemoStart"),
                Button("Stop demo camera", () => session.DemoCapture(false)).AutomationId("DemoStop")) : null
        );
    }

    private Element General() => VStack(12,
        Heading("General", "A small utility, out of your way."),
        Row(VStack(4, Copy("Appearance").SemiBold(), Description("Use the same theme for light controls and settings.")),
            ComboBox(["System", "Light", "Dark"], (int)session.Preferences.Appearance,
                index => session.SetAppearance((AppearanceMode)index)).Width(170).AutomationName("Appearance").AutomationId("Appearance")),
        Row(VStack(4, Copy("Start at sign-in").SemiBold(), Description("Start quietly in the tray. No administrator prompt.")),
            ToggleSwitch(session.Preferences.StartAtLogin, session.SetStartup).AutomationName("Start at sign-in").IsEnabled(!session.Isolated && !session.Demo)),
        Row(VStack(4, Copy("When the window closes").SemiBold(), Description("Stay in the tray. Camera preview stops.")), Copy("Keep running", 12)),
        Section("Privacy", Description("Background monitoring reads camera activity metadata. Explicit preview stays local and never records or uploads frames. The application sends no telemetry."))
    );

    private Element Diagnostics()
    {
        var settings = session.Preferences;
        return VStack(12,
        Heading("Diagnostics", "Connection and identity details for troubleshooting. Normal monitoring needs no helper."),
        Section("Camera monitor", VStack(8, Copy(session.SensorDetail, 12).AutomationId("SensorStatus"),
            Copy(session.AutomationDetail, 12),
            Description("Unknown camera state keeps owned lights unchanged."))),
        Section("Light connections", VStack(12, settings.Lights.Select(light => VStack(6,
            Copy(light.Name).SemiBold(), Copy($"{light.Host}:{light.Port}", 12), Copy(session.LightReading(light.Id), 12),
            Button("Test connection", () => session.Run(() => session.TestLightAsync(light))).IsEnabled(!session.Busy && !light.Tls))
                .WithKey(light.Id)).ToArray())),
        Section("Selected app identities", VStack(10,
            settings.Apps.Where(app => app.Enabled).Select(app => VStack(4, Copy(app.Name).SemiBold(), Copy(app.Key, 11)).WithKey(app.Key)).ToArray())),
        Section("Experimental tools", VStack(10,
            Description("The legacy ETW helper is for diagnostics, not a prerequisite for normal monitoring."),
            Button("Set up / repair helper (administrator)", () => session.Run(session.SetupAsync)).AutomationId("SetupMonitor")
                .IsEnabled(!session.Busy && !session.Isolated && !session.Demo)))
        );
    }
}
