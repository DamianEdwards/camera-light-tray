using CameraLightTray.Core;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace CameraLightTray.App.UI;

// THESIS: Equipment-first quick controls, not a miniature diagnostics dashboard.
// OWN-WORLD: Studio charcoal, muted blue, warm lit panels and Windows controls.
// STORY: See power and automation separately, adjust a light, open settings.
// FIRST VIEWPORT: 400-DIP tray flyout, two product rows, visible sliders and cog.
// FORM: User-approved Studio composition; native adaptation of the review mockup.
internal static class StudioStyle
{
    private static readonly Dictionary<string, SvgImageSource> Images = [];
    private static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();
    private static readonly Windows.UI.ViewManagement.UISettings UISettings = new();
    internal static bool HighContrast => Accessibility.HighContrast;
    internal static string Color(string value, bool background = false)
    {
        if (!HighContrast) return value;
        var color = UISettings.GetColorValue(background ? Windows.UI.ViewManagement.UIColorType.Background : Windows.UI.ViewManagement.UIColorType.Foreground);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }
    internal static TextBlockElement Text(string text, double size = 13, string color = "#B2BCC6") =>
        TextBlock(text).FontSize(size).Foreground(Color(color)).TextWrapping().AutomationName(text);
    internal static Element Glyph(string glyph, double size = 18) => Icon(FontIcon(glyph, "Segoe Fluent Icons", size));
    internal static Element Split(Element left, Element right) =>
        Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto], [left.Grid(), right.Grid(column: 1).VAlign(VerticalAlignment.Center)]);
    internal static Element Line() => Border(null).Height(1).Background(Color("#434A53"));
    private static Windows.UI.Color Rgb(byte r, byte g, byte b) => new() { A = 255, R = r, G = g, B = b };
    internal static void Accent(FrameworkElement element)
    {
        string[] keys = ["ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed"];
        if (HighContrast) { foreach (string key in keys) element.Resources.Remove(key); return; }
        var accent = new SolidColorBrush(Rgb(169, 202, 223));
        foreach (string key in keys)
            element.Resources[key] = accent;
    }
    internal static void SliderAppearance(Microsoft.UI.Xaml.Controls.Slider slider, bool temperature)
    {
        if (HighContrast)
        {
            foreach (string suffix in new[] { "", "PointerOver", "Pressed" })
            foreach (string key in new[] { "SliderTrackFill", "SliderTrackValueFill", "SliderThumbBackground", "SliderThumbBorderBrush" })
                slider.Resources.Remove(key + suffix);
            return;
        }
        var track = new LinearGradientBrush { StartPoint = new(0, 0.5), EndPoint = new(1, 0.5) };
        track.GradientStops.Add(new() { Offset = 0, Color = temperature ? Rgb(239, 202, 160) : Rgb(73, 85, 95) });
        track.GradientStops.Add(new() { Offset = 1, Color = temperature ? Rgb(165, 206, 220) : Rgb(202, 211, 218) });
        foreach (string suffix in new[] { "", "PointerOver", "Pressed" })
        {
            slider.Resources["SliderTrackFill" + suffix] = track;
            slider.Resources["SliderTrackValueFill" + suffix] = new SolidColorBrush(Rgb(0, 0, 0)) { Opacity = 0 };
            slider.Resources["SliderThumbBackground" + suffix] = new SolidColorBrush(Rgb(169, 190, 205));
            slider.Resources["SliderThumbBorderBrush" + suffix] = new SolidColorBrush(Rgb(235, 241, 245));
        }
    }
    internal static Element Art(LightDevice light, LightObservation observation, double width = 58)
    {
        if (HighContrast) return Glyph("\uE781", 32).Width(width).AutomationName(light.Name);
        string name = LightControls.Model(light) switch { LightModel.KeyLight => "KeyLight", LightModel.KeyLightAir => "KeyLightAir", _ => "UnknownLight" };
        string state = light.Tls || !observation.Fresh ? "unknown" : observation.State!.On == 1 ? "on" : "off";
        return Svg($"{name}.{state}.svg", width, width * 180 / 160)
            .AutomationName($"{light.Name}: {(state == "unknown" ? "power unknown" : state)}");
    }
    internal static ImageElement Svg(string file, double width, double height)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
        if (!Images.TryGetValue(file, out var source)) Images[file] = source = new SvgImageSource(new Uri(path));
        return Image(path).AutomationName(file).Width(width).Height(height).Set(image => image.Source = source);
    }
}

internal sealed class Studio(AppSession session) : Component
{
    public override Element Render()
    {
        var (_, refresh) = UseState(session.Revision);
        UseEffect(() =>
        {
            void Changed() => refresh(session.Revision);
            session.Changed += Changed;
            return () => session.Changed -= Changed;
        }, session);
        var settings = session.Preferences;
        void Settings() => session.OpenSettings?.Invoke();
        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto], [
            StudioStyle.Split(HStack(10, StudioStyle.Svg("AppIcon.svg", 25, 25).AccessibilityHidden(),
                StudioStyle.Text(session.Demo ? "Camera Light Tray · Demo" : "Camera Light Tray", 15, "#F0F3F5").SemiBold().VAlign(VerticalAlignment.Center)),
                Button(StudioStyle.Glyph("\uE713"), Settings).AutomationName("Open settings").AutomationId("OpenSettings")
                    .Width(34).Height(34).Padding(6).Background(StudioStyle.Color("Transparent", true)).BorderThickness(0))
                .Padding(18, 12).Grid(),
            VStack(10,
                StudioStyle.Line(),
                StudioStyle.Split(VStack(4,
                    StudioStyle.Text(session.ActivityTitle, 13, session.ActivityTitle == "Camera state unknown" ? "#EDC381" : "#F0F3F5").SemiBold()
                        .AutomationId("ActivityTitle"),
                    StudioStyle.Text(session.ActivityContext, 11)),
                    Button(session.Mode == ManualMode.Paused ? "Resume" : "Pause",
                        () => session.SetMode(session.Mode == ManualMode.Paused ? ManualMode.Automatic : ManualMode.Paused))
                        .FontSize(11).AutomationName("Pause or resume automation").AutomationId("PauseAutomation"))
                    .Padding(18, 0),
                !settings.AutomationEnabled ? Button("Enable automation", () => session.Enable(true)).AutomationId("TrayEnable").Margin(18, 0) : null,
                !string.IsNullOrEmpty(session.Error) ? StudioStyle.Text(session.Error, 11, "#EDC381").Margin(18, 0) : null,
                session.Isolated && !session.Demo ? StudioStyle.Text("Isolated preferences", 11).Margin(18, 0) : null,
                StudioStyle.Line()).Grid(row: 1),
            ScrollViewer(settings.Lights.Count == 0 ?
                VStack(14, StudioStyle.Glyph("\uE781", 34).HAlign(HorizontalAlignment.Center),
                    StudioStyle.Text("Bring your lights in", 18, "#F0F3F5").SemiBold(),
                    StudioStyle.Text("Choose your LAN and discover lights. New lights stay off until you select them."),
                    Button("Add lights", () => { session.Page = 3; Settings(); })).Padding(24) :
                VStack(0, settings.Lights.Select(light => (Element)Component<StudioLight, StudioLightProps>(new(session, light, session.Revision)).WithKey(light.Id)).ToArray()))
                .Grid(row: 2),
            VStack(0, StudioStyle.Line(),
                StudioStyle.Split(StudioStyle.Text($"{settings.Lights.Count(l => l.Enabled && !l.Tls)} lights set to auto", 11),
                    Button("Manage triggers  ›", () => { session.Page = 1; Settings(); })
                        .Background(StudioStyle.Color("Transparent", true)).BorderThickness(0).FontSize(11).Foreground(StudioStyle.Color("#A9CADF")))
                    .Padding(14, 8)).Grid(row: 3)
        ]).Background(StudioStyle.Color("#24282E", true)).Set(element =>
        {
            var root = (FrameworkElement)element;
            root.RequestedTheme = ElementTheme.Dark;
            StudioStyle.Accent(root);
        }).AutomationId("StudioFlyout");
    }
}

internal sealed record StudioLightProps(AppSession Session, LightDevice Light, int Revision);
internal sealed class StudioLight : Component<StudioLightProps>
{
    public override Element Render()
    {
        var session = Props.Session;
        var light = Props.Light;
        var observation = session.Observation(light.Id);
        bool fresh = observation.Fresh && !light.Tls;
        bool manual = session.IsManual(light.Id);
        var (brightness, setBrightness) = UseState(observation.State?.Brightness ?? 26);
        var (kelvin, setKelvin) = UseState(observation.State is { } current ?
            (int)(Math.Round(LightControls.Kelvin(current.Temperature) / 100d) * 100) : 4900);
        bool editing = session.IsEditing(light.Id);
        UseEffect(() =>
        {
            if (editing || observation.State is not { } state) return;
            setBrightness(state.Brightness);
            setKelvin(Math.Clamp((int)(Math.Round(LightControls.Kelvin(state.Temperature) / 100d) * 100), 2900, 7000));
        }, observation.State, editing);
        string power = light.Tls ? "Unsupported TLS model" : !fresh ? "Power unknown" : observation.State!.On == 1 ? "On" : "Off";
        Element Adjustment(string label, int value, int min, int max, Action<int> changed, string id, string unit)
        {
            string display = light.Tls ? "Unavailable" : observation.State is null ? "Not read" :
                $"{value:N0}{unit}" + (!fresh ? " · last known" : "");
            return VStack(0,
                StudioStyle.Split(StudioStyle.Text(label, 11), StudioStyle.Text(display, 11, "#F0F3F5")),
                Slider(value, min, max, number =>
                {
                    int next = (int)Math.Round(number);
                    changed(next);
                    if (!fresh) return;
                    if (label == "Brightness" && next != observation.State!.Brightness) session.AdjustLight(light, brightness: next);
                    if (label == "Temperature" && next != (int)(Math.Round(LightControls.Kelvin(observation.State!.Temperature) / 100d) * 100))
                        session.AdjustLight(light, kelvin: next);
                }).Height(26).MinHeight(0).IsEnabled(fresh && !session.Busy).AutomationName($"{light.Name} {label}").AutomationId(id)
                    .Set(slider =>
                    {
                        slider.StepFrequency = label == "Brightness" ? 1 : 100;
                        slider.IsThumbToolTipEnabled = false;
                        StudioStyle.SliderAppearance(slider, label == "Temperature");
                    }));
        }
        return Border(VStack(6,
            Grid([GridSize.Px(68), GridSize.Star(), GridSize.Auto], [GridSize.Auto], [
                StudioStyle.Art(light, observation).Grid(),
                VStack(5, StudioStyle.Text(light.Name, 14, "#F0F3F5").SemiBold(),
                    StudioStyle.Text(power + " · " + (manual ? "Manual" : light.Enabled ? "Automatic" : "Not automated"), 11))
                    .VAlign(VerticalAlignment.Center).Grid(column: 1),
                Button(StudioStyle.Glyph("\uE7E8"), () => session.Run(() => session.ToggleLightAsync(light)))
                    .Width(36).Height(36).Padding(8).CornerRadius(7)
                    .Background(StudioStyle.Color(fresh && observation.State!.On == 1 ? "#A9CADF" : "#30353C", true))
                    .Foreground(StudioStyle.Color(fresh && observation.State!.On == 1 ? "#183345" : "#B2BCC6"))
                    .AutomationName(fresh ? $"Turn {light.Name} {(observation.State!.On == 1 ? "off" : "on")}" : $"{light.Name}: power unknown")
                    .AutomationId("Power-" + light.Id).IsEnabled(fresh && !session.Busy).Grid(column: 2).VAlign(VerticalAlignment.Center)
            ]),
            StudioStyle.Split(HStack(8,
                ToggleSwitch(light.Enabled, value => session.SelectLight(light, value)).Width(44).MinWidth(0).Height(28).MinHeight(0).IsEnabled(!light.Tls)
                    .AutomationName("Automate " + light.Name).AutomationId("Auto-" + light.Id)
                    .Set(toggle => { toggle.OnContent = null; toggle.OffContent = null; }),
                StudioStyle.Text(manual ? "Manual override" : "Auto with camera", 11).VAlign(VerticalAlignment.Center)),
                manual ? Button("Resume auto", () => session.Run(() => session.ResumeLightAsync(light)))
                    .FontSize(11).AutomationId("Resume-" + light.Id).Background(StudioStyle.Color("Transparent", true)).BorderThickness(0)
                    .Foreground(StudioStyle.Color("#A9CADF")) : Empty()),
            Adjustment("Brightness", brightness, 0, 100, setBrightness, "Brightness-" + light.Id, "%"),
            Adjustment("Temperature", kelvin, 2900, 7000, setKelvin, "Temperature-" + light.Id, " K"),
            light.Tls ? StudioStyle.Text("This model uses TLS. Control is not supported yet.", 11, "#EDC381") :
                !fresh ? StudioStyle.Text(observation.Error is null ? "Checking this light…" : "Reconnecting automatically. Other lights still work.", 11, "#EDC381") :
                editing ? StudioStyle.Text("Applying adjustment…", 11) : null
        )).Padding(16, 12).BorderBrush(StudioStyle.Color("#434A53")).BorderThickness(bottom: 1);
    }
}
