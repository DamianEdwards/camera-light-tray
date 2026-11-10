using CameraLightTray.App.Native;
using CameraLightTray.App.UI;
using CameraLightTray.Core;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using System.Runtime.InteropServices.WindowsRuntime;
using CameraLightTray.App.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CameraLightTray.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool smoke = args.Contains("--smoke-test");
        bool demo = smoke || args.Contains("--demo");
        bool isolated = args.Contains("--isolated");
        string? explicitDirectory = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length) explicitDirectory = args[++i];
            else if (args[i] is not ("--smoke-test" or "--demo" or "--isolated" or "--startup" or "--settings"))
            { Tray.MessageBox(0, "Unknown or incomplete argument: " + args[i], "Camera Light Tray", 0x10); return 1; }
        }
        if ((isolated || demo) && string.IsNullOrWhiteSpace(explicitDirectory))
        { Tray.MessageBox(0, "Isolated/demo/smoke runs require --data-dir <separate-directory>.", "Camera Light Tray", 0x10); return 1; }
        string directory = explicitDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CameraLightTray");
        ReactorWindow? window = null;
        ReactorWindow? flyout = null;
        Tray? tray = null;
        AppSession? session = null;
        bool exiting = false;
        int exitCode = 0;
        ILoggerFactory? logging = null;
        ILogger log = NullLogger.Instance;
        string? loggingFailure = null;
        try
        {
            Directory.CreateDirectory(directory);
            using var instance = new FileStream(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var provider = new FileLoggerProvider(Path.Combine(directory, "logs"), failure =>
            {
                Interlocked.Exchange(ref loggingFailure, failure);
                if (failure is not null) Console.Error.WriteLine(failure);
                ReactorApp.UIDispatcher?.TryEnqueue(() => session?.SetLoggingFailure(failure));
            });
            logging = new LoggerFactory();
            logging.AddProvider(provider);
            log = logging.CreateLogger("CameraLightTray.Lifecycle");
            AppSession.DispatchLog = log;
            log.LogInformation("Application starting; version={Version}, runtime={Runtime}, isolated={Isolated}, demo={Demo}",
                System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Program).Assembly)?.InformationalVersion,
                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, isolated, demo);
            session = new(new(directory), isolated, demo, logging);
            ReactorApp.ShutdownPolicy = ShutdownPolicy.Explicit;
            ReactorApp.Run(context =>
            {
                bool? captionDark = null;
                void ApplyCaption()
                {
                    if (window is null || exiting || captionDark == AppTheme.Dark) return;
                    Tray.SetDarkCaption(WinRT.Interop.WindowNative.GetWindowHandle(window.NativeWindow), AppTheme.Dark);
                    captionDark = AppTheme.Dark;
                }
                session.ShellChanged += ApplyCaption;
                void ShowSettings()
                {
                    if (window is null)
                    {
                        window = ReactorApp.OpenWindow(new WindowSpec
                        {
                            Title = demo ? "Camera Light Tray - Synthetic demo" : "Camera Light Tray",
                            Width = 940, Height = 780, MinWidth = 620, MinHeight = 500,
                            Icon = WindowIcon.FromPath(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")),
                            ActivateOnOpen = !smoke, Backdrop = BackdropChoice.Of(BackdropKind.Mica)
                        }, () => new Dashboard(session));
                        window.Closing += (_, e) => { if (!exiting) { e.Cancel = true; session.HidePreview(); window.Hide(); } };
                    }
                    ApplyCaption();
                    window.Show();
                    if (!smoke) window.Activate();
                }
                void PlaceFlyout()
                {
                    if (exiting || flyout is null) return;
                    int? height = null;
                    if (Find(flyout.NativeWindow.Content, "StudioFlyout") is Grid { ActualHeight: > 0 } root &&
                        Find(root, "StudioLights") is ScrollViewer { ActualHeight: > 0, Content: FrameworkElement equipment } lights &&
                        equipment.DesiredSize.Height > 0)
                    {
                        // A star-row scroller's constrained DesiredSize cannot grow an auto-sized window.
                        height = (int)Math.Ceiling(root.RowDefinitions[0].ActualHeight +
                            root.RowDefinitions[1].ActualHeight + root.RowDefinitions[3].ActualHeight +
                            Math.Min(equipment.DesiredSize.Height, lights.MaxHeight));
                    }
                    tray?.Place(flyout, height);
                }
                ReactorWindow ShowFlyout()
                {
                    if (flyout is null)
                    {
                        flyout = ReactorApp.OpenWindow(new WindowSpec
                        {
                            Title = demo ? "Camera Light Tray - Studio demo" : "Camera Light Tray - Light controls",
                            Width = 400, Style = WindowStyle.None,
                            MaxHeight = 730,
                            ResizeMode = WindowResizeMode.NoResize, ShowInTaskbar = false, ShowInSwitcher = false,
                            CornerStyle = WindowCornerStyle.Rounded, ActivateOnOpen = !smoke
                        }, () => new Studio(session));
                        flyout.Closing += (_, e) => { if (!exiting) { e.Cancel = true; flyout.Hide(); } };
                        flyout.Deactivated += (_, _) => { if (!smoke && !demo) flyout.Hide(); };
                        flyout.NativeWindow.SizeChanged += (_, _) => PlaceFlyout();
                        ((FrameworkElement)flyout.NativeWindow.Content).LayoutUpdated += (_, _) => PlaceFlyout();
                        flyout.NativeWindow.Content.KeyDown += (_, e) =>
                        {
                            if (e.Key == Windows.System.VirtualKey.Escape) { flyout.Hide(); e.Handled = true; }
                        };
                    }
                    PlaceFlyout();
                    flyout.Show();
                    if (!smoke) flyout.Activate();
                    return flyout;
                }
                void TrayClicked()
                {
                    if (flyout is not null && Tray.IsWindowVisible(WinRT.Interop.WindowNative.GetWindowHandle(flyout.NativeWindow)) != 0) flyout.Hide();
                    else ShowFlyout();
                }
                session.OpenSettings = () => { flyout?.Hide(); ShowSettings(); };
                async void Exit()
                {
                    if (exiting) return;
                    exiting = true;
                    try { await session.StopAsync(); }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Application shutdown failed");
                        File.WriteAllText(Path.Combine(directory, "shutdown-error.txt"), ex.Message);
                        exitCode = 1;
                        if (!smoke) Tray.MessageBox(0, "Some owned light states could not be restored. Recovery is retained for next launch.\n" + ex.Message,
                            "Camera Light Tray", 0x30);
                    }
                    finally { tray?.Dispose(); tray = null; flyout?.Close(); window?.Close(); ReactorApp.Exit(exitCode); }
                }
                Application.Current.UnhandledException += (_, e) =>
                {
                    log.LogError(e.Exception, "Unhandled UI operation exception");
                    session.Report("UI operation failed: " + e.Exception.Message);
                    e.Handled = true;
                    if (smoke)
                    {
                        File.WriteAllText(Path.Combine(directory, "smoke-result.txt"), "FAIL: " + e.Exception);
                        exitCode = 1; Exit();
                    }
                };
                tray = new(TrayClicked, () => session.SetMode(session.Mode == ManualMode.Paused ? ManualMode.Automatic : ManualMode.Paused),
                    Exit, session.Report);
                tray.Resumed += session.Resume;
                session.SetLoggingFailure(Volatile.Read(ref loggingFailure));
                session.Start();
                if (smoke || args.Contains("--settings")) ShowSettings();
                else if (!args.Contains("--startup")) ShowFlyout();
                if (smoke) _ = SmokeAsync(window!, ShowFlyout, tray, session, directory, code => { exitCode = code; Exit(); });
            });
            return exitCode;
        }
        catch (Exception ex)
        {
            exitCode = 1;
            log.LogCritical(ex, "Application failed");
            if (smoke) File.WriteAllText(Path.Combine(directory, "smoke-result.txt"), "FAIL: " + ex);
            else Tray.MessageBox(0, ex.Message, "Camera Light Tray could not start", 0x10);
            return 1;
        }
        finally
        {
            tray?.Dispose();
            log.LogInformation("Application stopped; exitCode={ExitCode}", exitCode);
            AppSession.DispatchLog = NullLogger.Instance;
            logging?.Dispose();
        }
    }

    private static async Task SmokeAsync(ReactorWindow window, Func<ReactorWindow> showFlyout, Tray tray, AppSession session, string directory, Action<int> finish)
    {
        try
        {
            await Task.Delay(1500);
            await OnUI(() =>
            {
                var enable = Find(window.NativeWindow.Content, "AutomationEnabled") as ToggleSwitch
                    ?? throw new InvalidOperationException("Reactor automation toggle was not rendered.");
                if (enable.IsOn || session.Preferences.AutomationEnabled) throw new InvalidOperationException("Automation unexpectedly starts enabled.");
                enable.IsOn = true;
                Invoke(window, "DemoStart");
            });
            await Task.Delay(1500);
            await OnUI(() =>
            {
                if (!session.Preferences.AutomationEnabled || !session.Apps.Any(a => a.Status == CameraStatus.Active))
                    throw new InvalidOperationException("Synthetic UI activation failed.");
                if (!session.AutomationDetail.Contains("lights on", StringComparison.Ordinal))
                    throw new InvalidOperationException("Synthetic camera did not activate simulated light.");
            });
            var lit = await OnUIWindow(showFlyout);
            await Task.Delay(300);
            await OnUI(() =>
            {
                double scale = lit.NativeWindow.Content.XamlRoot.RasterizationScale;
                lit.NativeWindow.AppWindow.Resize(new((int)(400 * scale), (int)(320 * scale)));
            });
            var readbackWait = System.Diagnostics.Stopwatch.StartNew();
            while (session.Preferences.Lights.Any(light => session.Observation(light.Id).State?.On != 1))
            {
                if (readbackWait.Elapsed > TimeSpan.FromSeconds(10)) throw new InvalidOperationException("Activated demo lights did not receive on readbacks.");
                await Task.Delay(100);
            }
            await Task.Delay(300);
            await OnUIAsync(() => CaptureAsync(lit, Path.Combine(directory, "studio-on.png")));
            await OnUI(() =>
            {
                var root = (FrameworkElement)lit.NativeWindow.Content;
                var last = Find(root, "Temperature-demo:air") as FrameworkElement ??
                    throw new InvalidOperationException("Second light's temperature control is missing.");
                var bounds = last.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, last.ActualWidth, last.ActualHeight));
                if (bounds.Bottom > root.ActualHeight - 30)
                    throw new InvalidOperationException($"Both lights' sliders must fit in the Studio flyout without scrolling: slider bottom={bounds.Bottom:0.##}; root height={root.ActualHeight:0.##}; window height={lit.NativeWindow.AppWindow.Size.Height}; maximum={lit.Spec.MaxHeight:0.##}; scale={root.XamlRoot.RasterizationScale:0.##}.");
                if (Find(root, "Power-demo:light") is not Button power ||
                    Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(power) != "Turn Key Light off")
                    throw new InvalidOperationException("Studio light rows must refresh their displayed power state from live readbacks.");
                var row = Find(root, "LightRow-demo:air") as FrameworkElement ?? throw new InvalidOperationException("Second light row missing.");
                var footer = Find(root, "StudioFooter") as FrameworkElement ?? throw new InvalidOperationException("Studio footer missing.");
                var rowBounds = row.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, row.ActualWidth, row.ActualHeight));
                var footerBounds = footer.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, footer.ActualWidth, footer.ActualHeight));
                if (Math.Abs(footerBounds.Y - rowBounds.Bottom) > 2)
                    throw new InvalidOperationException($"Studio must neither clip its last light nor leave an empty footer band: row bottom={rowBounds.Bottom:0.##}; footer top={footerBounds.Y:0.##}.");
                CheckPopupParts(lit, session);
            });
            await OnUI(() =>
            {
                lit.Hide();
                Invoke(window, "DemoStop"); Invoke(window, "OpenRules");
            });
            await Task.Delay(3200);
            await OnUI(() =>
            {
                Invoke(window, "SaveRules");
                if (!session.AutomationDetail.Contains("restored", StringComparison.Ordinal))
                    throw new InvalidOperationException("Synthetic release did not restore prior state.");
                Invoke(window, "OpenDiagnostics");
            });
            await Task.Delay(250);
            await OnUI(() =>
            {
                if (Find(window.NativeWindow.Content, "SetupMonitor") is not Button { IsEnabled: false })
                    throw new InvalidOperationException("Setup must be disabled in synthetic smoke.");
                Tray.CloseForSmoke(WinRT.Interop.WindowNative.GetWindowHandle(window.NativeWindow));
            });
            await Task.Delay(150);
            await OnUI(() =>
            {
                nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window.NativeWindow);
                if (Tray.IsWindowVisible(handle) != 0) throw new InvalidOperationException("Closing must hide the UI.");
                tray.SelectForSmoke();
            });
            await Task.Delay(250);
            ReactorWindow? popup = null;
            await OnUI(() =>
            {
                popup = showFlyout();
            });
            await Task.Delay(300);
            await OnUI(() =>
            {
                if (Find(popup!.NativeWindow.Content, "StudioFlyout") is null)
                    throw new InvalidOperationException("Tray selection must open the Studio flyout. " + session.Error +
                        " Root=" + popup.NativeWindow.Content.GetType().Name + "; children=" +
                        VisualTreeHelper.GetChildrenCount(popup.NativeWindow.Content));
                if (Find(popup.NativeWindow.Content, "Brightness-demo:light") is not Slider slider)
                    throw new InvalidOperationException("Studio slider missing.");
                slider.Value = 35;
            });
            await Task.Delay(1000);
            await OnUI(() =>
            {
                if (session.Observation("demo:light").State?.Brightness != 35 || !session.IsManual("demo:light"))
                    throw new InvalidOperationException("Native slider did not perform a manual adjustment.");
                Invoke(popup!, "Resume-demo:light");
            });
            await Task.Delay(500);
            await OnUI(() =>
            {
                if (session.IsManual("demo:light")) throw new InvalidOperationException("Native resume-auto control did not clear the manual override.");
                Invoke(popup!, "Power-demo:light");
            });
            await Task.Delay(500);
            await OnUI(() => Invoke(popup!, "Power-demo:light"));
            await Task.Delay(500);
            await OnUI(() =>
            {
                if (session.Observation("demo:light").State?.On != 0)
                    throw new InvalidOperationException("Native power controls did not toggle the simulated light on and off.");
            });
            await CheckPopupInteractionsAsync(popup!, session, directory);
            await OnUI(() => Invoke(popup!, "OpenSettings"));
            await Task.Delay(250);
            await OnUI(() =>
            {
                if (Tray.IsWindowVisible(WinRT.Interop.WindowNative.GetWindowHandle(window.NativeWindow)) == 0)
                    throw new InvalidOperationException("Settings cog did not reopen full settings.");
                Invoke(window, "OpenApps");
            });
            await Task.Delay(250);
            await OnUIAsync(() => CaptureAsync(window, Path.Combine(directory, "studio-settings.png")));
            await OnUI(() =>
            {
                window.Update(window.Spec with { Height = 500 });
                Invoke(window, "OpenRules");
            });
            await Task.Delay(250);
            ScrollViewer? originalScroll = null;
            await OnUI(() =>
            {
                originalScroll = Find(window.NativeWindow.Content, "SettingsScroll") as ScrollViewer ??
                    throw new InvalidOperationException("Settings scroller missing.");
                if (originalScroll.ScrollableHeight < 100) throw new InvalidOperationException("Scroll regression fixture must overflow.");
                originalScroll.ChangeView(null, 100, null, true);
            });
            await Task.Delay(250);
            await OnUI(() =>
            {
                if (originalScroll!.VerticalOffset < 99) throw new InvalidOperationException("Could not scroll the source page.");
                for (int i = 0; i < 10; i++) session.Notify();
            });
            await Task.Delay(250);
            await OnUI(() =>
            {
                if (!ReferenceEquals(originalScroll, Find(window.NativeWindow.Content, "SettingsScroll")) || originalScroll!.VerticalOffset < 99)
                    throw new InvalidOperationException("Background updates must preserve the current page's scroll position.");
                Invoke(window, "OpenDiagnostics");
            });
            await Task.Delay(250);
            await OnUI(() =>
            {
                if (Find(window.NativeWindow.Content, "SettingsScroll") is not ScrollViewer { VerticalOffset: 0 })
                    throw new InvalidOperationException("Navigation must start the destination page at the top.");
                Invoke(window, "OpenRules");
            });
            await Task.Delay(250);
            await OnUI(() =>
            {
                if (Find(window.NativeWindow.Content, "SettingsScroll") is not ScrollViewer { VerticalOffset: 0 })
                    throw new InvalidOperationException("Returning to a page must also reset its scroll position.");
                window.Update(window.Spec with { Height = 780 });
            });
            await CheckAppearanceAndLayoutAsync(window, popup!, session, directory);
            await OnUI(() =>
            {
                File.WriteAllText(Path.Combine(directory, "smoke-result.txt"),
                    "PASS: Native Studio flyout and settings, stable popup hover, debounced background edits with retained slider focus and layout, cancellation-safe readback health, confirmed brightness/temperature artwork, unclipped controls, page-scroll reset, shared appearance, camera automation and safe setup defaults.");
                finish(0);
            });
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "smoke-result.txt"), "FAIL: " + ex);
            await OnUI(() => finish(1));
        }
    }
    private static Task OnUI(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AppSession.Post(() => { try { action(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        return completion.Task;
    }
    private static async Task CheckPopupInteractionsAsync(ReactorWindow popup, AppSession session, string directory)
    {
        Button[] buttons = [];
        object[] content = [];
        Windows.UI.Color[] painted = [];
        await OnUI(() =>
        {
            buttons = new[] { "OpenSettings", "Power-demo:light" }.Select(id =>
                Find(popup.NativeWindow.Content, id) as Button ?? throw new InvalidOperationException("Popup button missing.")).ToArray();
            content = buttons.Select(button => button.Content).ToArray();
            foreach (var button in buttons)
                if (!VisualStateManager.GoToState(button, "PointerOver", false))
                    throw new InvalidOperationException("Popup hover state unavailable.");
        });
        await Task.Delay(150);
        await OnUI(() =>
        {
            painted = buttons.Select(button => (ButtonPresenter(button).Background as SolidColorBrush ??
                throw new InvalidOperationException("Button hover background is not a solid brush.")).Color).ToArray();
            for (int i = 0; i < 20; i++) session.Notify();
        });
        await Task.Delay(350);
        await OnUI(() =>
        {
            for (int i = 0; i < buttons.Length; i++)
                if (!ReferenceEquals(buttons[i].Content, content[i]) ||
                    (ButtonPresenter(buttons[i]).Background as SolidColorBrush)?.Color != painted[i] ||
                    !Descendants(buttons[i]).OfType<FrameworkElement>()
                        .SelectMany(VisualStateManager.GetVisualStateGroups)
                        .Any(group => group.Name == "CommonStates" && group.CurrentState?.Name == "PointerOver"))
                    throw new InvalidOperationException("Background updates reset popup button content or hover state.");
            var powerIcon = Find(buttons[1], "PowerGlyph-demo:light") as Image;
            if (powerIcon?.Source is not Microsoft.UI.Xaml.Media.Imaging.SvgImageSource ||
                powerIcon.ActualWidth < 20 || powerIcon.ActualHeight < 20 || powerIcon.IsHitTestVisible)
                throw new InvalidOperationException("Power must use the fully contained vector icon, not a cropped font glyph.");
            CheckButtonContentSlot(buttons[1], powerIcon);
            Invoke(popup, "Power-demo:light");
        });
        await Task.Delay(600);
        await OnUI(() =>
        {
            var power = Find(popup.NativeWindow.Content, "Power-demo:light") as Button ??
                throw new InvalidOperationException("Power button missing.");
            VisualStateManager.GoToState(power, "PointerOver", false);
        });
        await Task.Delay(150);
        await OnUI(() =>
        {
            var power = Find(popup.NativeWindow.Content, "Power-demo:light") as Button ??
                throw new InvalidOperationException("Power button missing.");
            CheckPowerButtonContrast(power);
            CheckButtonContentSlot(power, Find(power, "PowerGlyph-demo:light") as FrameworkElement ??
                throw new InvalidOperationException("Power icon missing."));
        });
        Slider? slider = null;
        double height = 0;
        int writes = 0;
        Windows.UI.Color before = default;
        Windows.UI.Color Panel()
        {
            var panel = Find(popup.NativeWindow.Content, "LightPanel-demo:light") as Border ??
                throw new InvalidOperationException("Confirmed on-state light panel missing.");
            return (panel.Background as SolidColorBrush ?? throw new InvalidOperationException("Light panel color missing.")).Color;
        }
        void CheckFocus()
        {
            var root = (FrameworkElement)popup.NativeWindow.Content;
            if (!ReferenceEquals(slider, Find(root, AutomationProperties.GetAutomationId(slider!))) ||
                !slider!.IsEnabled || !Descendants(slider).Contains(FocusManager.GetFocusedElement(root.XamlRoot)))
                throw new InvalidOperationException("Slider focus/identity was lost while editing in the background.");
            if (Math.Abs(root.ActualHeight - height) > 1 ||
                Descendants(root).OfType<TextBlock>().Any(text => text.Text.Contains("Applying adjustment", StringComparison.Ordinal)))
                throw new InvalidOperationException("Background slider edits must not change popup height or add applying labels.");
            if (session.Observation("demo:light").Error is not null)
                throw new InvalidOperationException("Superseded slider cancellation must not mark the light unreachable.");
        }
        await OnUI(() =>
        {
            session.DemoLightDelayMilliseconds = 150;
            slider = Find(popup.NativeWindow.Content, "Brightness-demo:light") as Slider ??
                throw new InvalidOperationException("Brightness slider missing.");
            height = ((FrameworkElement)popup.NativeWindow.Content).ActualHeight;
            writes = session.DemoLightWriteCount;
            before = Panel();
            if (!slider.Focus(FocusState.Keyboard)) throw new InvalidOperationException("Could not focus native slider.");
            slider.Value = 36;
        });
        await Task.Delay(320);
        await OnUI(() => { slider!.Value = 37; session.Notify(); });
        await Task.Delay(320);
        await OnUI(CheckFocus);
        for (int value = 38; value <= 41; value++)
        {
            int next = value;
            await OnUI(() => { slider!.Value = next; session.Notify(); CheckFocus(); });
            await Task.Delay(40);
        }
        await OnUI(() =>
        {
            if (session.DemoLightWriteCount != writes || Panel() != before)
                throw new InvalidOperationException("Pending values must debounce without optimistic artwork or premature writes.");
        });
        await Task.Delay(1200);
        await OnUI(() =>
        {
            CheckFocus();
            if (session.DemoLightWriteCount != writes + 1 || session.Observation("demo:light").State?.Brightness != 41 || Panel() == before)
                throw new InvalidOperationException("Debounced slider intent must apply once and update artwork from readback.");
            slider = Find(popup.NativeWindow.Content, "Temperature-demo:light") as Slider ??
                throw new InvalidOperationException("Temperature slider missing.");
            if (!slider.Focus(FocusState.Keyboard)) throw new InvalidOperationException("Could not focus temperature.");
            slider.Value = 2900;
            session.AdjustLight(session.Preferences.Lights.Single(l => l.Id == "demo:light"), brightness: 100);
        });
        await Task.Delay(1200);
        Windows.UI.Color warm = default;
        await OnUI(() => { CheckFocus(); warm = Panel(); });
        await OnUIAsync(() => CaptureAsync(popup, Path.Combine(directory, "studio-controls-warm.png")));
        await OnUI(() => slider!.Value = 7000);
        await Task.Delay(1200);
        await OnUI(() =>
        {
            CheckFocus();
            var cool = Panel();
            if (cool.B <= warm.B || cool.R >= warm.R)
                throw new InvalidOperationException("Confirmed warm and cool light states must tint the product panel differently.");
            session.DemoLightDelayMilliseconds = 0;
        });
        await OnUIAsync(() => CaptureAsync(popup, Path.Combine(directory, "studio-controls-cool.png")));
        await OnUI(() => Invoke(popup, "Power-demo:light"));
        await Task.Delay(500);
    }
    private static ContentPresenter ButtonPresenter(Button button) =>
        Descendants(button).OfType<ContentPresenter>().FirstOrDefault(p => p.Name == "ContentPresenter") ??
            throw new InvalidOperationException("Native button content presenter missing.");
    private static void CheckPowerButtonContrast(Button button)
    {
        var background = (ButtonPresenter(button).Background as SolidColorBrush ??
            throw new InvalidOperationException("Power hover background missing.")).Color;
        byte[] rgb = Convert.FromHexString(StudioStyle.Color("#183345")[1..]);
        var foreground = new Windows.UI.Color { A = 255, R = rgb[0], G = rgb[1], B = rgb[2] };
        static double Luminance(Windows.UI.Color color)
        {
            static double Linear(byte value) => value / 255d <= 0.04045 ? value / 255d / 12.92 : Math.Pow((value / 255d + 0.055) / 1.055, 2.4);
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        double a = Luminance(background), b = Luminance(foreground);
        if ((Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05) < 3)
            throw new InvalidOperationException($"On-state power icon loses contrast against its actual painted hover background: background={background}, icon={foreground}, resource={(button.Resources["ButtonBackgroundPointerOver"] as SolidColorBrush)?.Color}.");
    }
    private static void CheckButtonContentSlot(Button button, FrameworkElement icon)
    {
        var presenter = ButtonPresenter(button);
        var bounds = icon.TransformToVisual(presenter).TransformBounds(new Windows.Foundation.Rect(0, 0, icon.ActualWidth, icon.ActualHeight));
        double left = presenter.Padding.Left + presenter.BorderThickness.Left;
        double top = presenter.Padding.Top + presenter.BorderThickness.Top;
        double right = presenter.ActualWidth - presenter.Padding.Right - presenter.BorderThickness.Right;
        double bottom = presenter.ActualHeight - presenter.Padding.Bottom - presenter.BorderThickness.Bottom;
        if (bounds.Left < left - 0.1 || bounds.Top < top - 0.1 || bounds.Right > right + 0.1 || bounds.Bottom > bottom + 0.1)
            throw new InvalidOperationException($"Power icon exceeds its actual button content slot: icon={bounds}; slot={left},{top}..{right},{bottom}; padding={presenter.Padding}; border={presenter.BorderThickness}.");
    }
    private static async Task CheckAppearanceAndLayoutAsync(ReactorWindow window, ReactorWindow popup, AppSession session, string directory)
    {
        await OnUI(() => Invoke(window, "OpenGeneral"));
        await Task.Delay(150);
        foreach (var mode in new[] { AppearanceMode.Light, AppearanceMode.Dark, AppearanceMode.System })
        {
            await OnUI(() =>
            {
                var selector = Find(window.NativeWindow.Content, "Appearance") as ComboBox ??
                    throw new InvalidOperationException("System/Light/Dark appearance selector is missing.");
                selector.SelectedIndex = (int)mode;
                popup.Show();
            });
            await Task.Delay(300);
            await OnUI(() =>
            {
                var settingsRoot = Find(window.NativeWindow.Content, "Dashboard") as FrameworkElement;
                var popupRoot = Find(popup.NativeWindow.Content, "StudioFlyout") as FrameworkElement;
                if (settingsRoot?.RequestedTheme != AppTheme.Element || popupRoot?.RequestedTheme != AppTheme.Element)
                    throw new InvalidOperationException("Both windows must use the same selected appearance.");
                if (session.Preferences.Appearance != mode)
                    throw new InvalidOperationException("Appearance preference did not persist.");
                CheckSettingsToggle(window, "StartupEnabled", "Appearance");
            });
            if (mode != AppearanceMode.System)
            {
                await OnUI(() => Invoke(window, "OpenRules"));
                await Task.Delay(200);
                foreach (bool enabled in new[] { false, true })
                {
                    await OnUI(() =>
                    {
                        var toggle = Find(window.NativeWindow.Content, "AutomationEnabled") as ToggleSwitch ??
                            throw new InvalidOperationException("Automation toggle missing.");
                        toggle.IsOn = enabled;
                    });
                    await Task.Delay(200);
                    await OnUI(() => CheckSettingsToggle(window, "AutomationEnabled", "ActivationDelay"));
                }
                await OnUIAsync(() => CaptureAsync(window, Path.Combine(directory, $"settings-automation-{mode.ToString().ToLowerInvariant()}.png")));
                await OnUI(() => Invoke(window, "OpenGeneral"));
                await Task.Delay(200);
                await OnUI(() =>
                {
                    foreach (var light in session.Preferences.Lights)
                    {
                        foreach (string prefix in new[] { "Auto-", "Brightness-", "Temperature-" })
                        {
                            var control = Find(popup.NativeWindow.Content, prefix + light.Id) as Control ??
                                throw new InvalidOperationException("Native popup control missing.");
                            if (!VisualStateManager.GoToState(control, "PointerOver", false))
                                throw new InvalidOperationException("Native popup hover state missing.");
                        }
                    }
                });
                await Task.Delay(250);
                await OnUI(() => CheckPopupParts(popup, session));
                await OnUIAsync(() => CaptureAsync(popup, Path.Combine(directory, $"studio-{mode.ToString().ToLowerInvariant()}.png")));
                await OnUIAsync(() => CaptureAsync(window, Path.Combine(directory, $"settings-{mode.ToString().ToLowerInvariant()}.png")));
            }
        }
        await OnUI(() => Invoke(window, "OpenCameras"));
        await Task.Delay(200);
        await OnUI(() =>
        {
            var root = window.NativeWindow.Content;
            var glyph = Find(root, "CameraGlyph-demo:camera") as FrameworkElement ?? throw new InvalidOperationException("Camera glyph missing.");
            var label = Find(root, "CameraName-demo:camera") as FrameworkElement ?? throw new InvalidOperationException("Camera label missing.");
            var a = glyph.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, glyph.ActualWidth, glyph.ActualHeight));
            var b = label.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, label.ActualWidth, label.ActualHeight));
            if (Math.Abs(a.Y + a.Height / 2 - b.Y - b.Height / 2) > 1)
                throw new InvalidOperationException("Camera glyph and label must align on their vertical center.");
            if (Descendants(root).Any(element => element is Expander))
                throw new InvalidOperationException("Everyday settings must not depend on expanding regions.");
        });
        await OnUIAsync(() => CaptureAsync(window, Path.Combine(directory, "settings-cameras.png")));
        await OnUI(() => Invoke(window, "OpenApps"));
        await Task.Delay(200);
        int renders = 0;
        Button? navigation = null;
        await OnUI(() =>
        {
            navigation = Find(window.NativeWindow.Content, "OpenLights") as Button ?? throw new InvalidOperationException("Navigation item missing.");
            if (!VisualStateManager.GoToState(navigation, "PointerOver", false))
                throw new InvalidOperationException("Native navigation hover state unavailable.");
            renders = session.SettingsShellRenders;
            for (int i = 0; i < 20; i++) session.Notify();
        });
        await Task.Delay(650);
        await OnUI(() =>
        {
            if (session.SettingsShellRenders != renders)
                throw new InvalidOperationException("Background updates must not re-render the navigation shell.");
            bool hovering = Descendants(navigation!).OfType<FrameworkElement>()
                .SelectMany(element => VisualStateManager.GetVisualStateGroups(element))
                .Any(group => group.Name == "CommonStates" && group.CurrentState?.Name == "PointerOver");
            if (!hovering) throw new InvalidOperationException("Background updates reset the native navigation hover state.");
        });
        await OnUIAsync(() => CaptureAsync(window, Path.Combine(directory, "studio-settings.png")));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void CheckSettingsToggle(ReactorWindow window, string id, string alignedWith)
    {
        var root = window.NativeWindow.Content;
        var toggle = Find(root, id) as ToggleSwitch ?? throw new InvalidOperationException("Settings toggle missing: " + id);
        var reference = Find(root, alignedWith) as FrameworkElement ?? throw new InvalidOperationException("Settings alignment reference missing.");
        var label = Descendants(toggle).OfType<FrameworkElement>()
            .FirstOrDefault(element => element.Name == (toggle.IsOn ? "OnContentPresenter" : "OffContentPresenter")) ??
            throw new InvalidOperationException("Native toggle state label missing.");
        var bounds = label.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, label.ActualWidth, label.ActualHeight));
        var referenceBounds = reference.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, reference.ActualWidth, reference.ActualHeight));
        if (Math.Abs(bounds.Right - referenceBounds.Right) > 1)
            throw new InvalidOperationException($"Settings toggle must visibly align with the other row controls: {id} right={bounds.Right:0.##}; reference={referenceBounds.Right:0.##}.");
        var track = Descendants(toggle).OfType<FrameworkElement>().FirstOrDefault(element => element.Name == "OuterBorder") ??
            throw new InvalidOperationException("Native toggle track missing.");
        var trackBounds = track.TransformToVisual(toggle).TransformBounds(new Windows.Foundation.Rect(0, 0, track.ActualWidth, track.ActualHeight));
        if (trackBounds.Y < -0.5 || trackBounds.Bottom > toggle.ActualHeight + 0.5)
            throw new InvalidOperationException("Right-aligned settings switches must retain their native height.");
    }
    private static void CheckPopupParts(ReactorWindow window, AppSession session)
    {
        var root = window.NativeWindow.Content;
        foreach (var light in session.Preferences.Lights)
        {
            var toggle = Find(root, "Auto-" + light.Id) as FrameworkElement ??
                throw new InvalidOperationException("Automation switch missing.");
            foreach (string name in new[] { "OuterBorder", "SwitchKnobOn", "SwitchKnobOff" })
                Fits(toggle, name);
            foreach (string prefix in new[] { "Brightness-", "Temperature-" })
            {
                var slider = Find(root, prefix + light.Id) as FrameworkElement ??
                    throw new InvalidOperationException("Popup slider missing.");
                Fits(slider, "HorizontalTemplate");
                Fits(slider, "HorizontalThumb");
            }
        }
        static void Fits(FrameworkElement control, string name)
        {
            var part = Descendants(control).OfType<FrameworkElement>().FirstOrDefault(element => element.Name == name) ??
                throw new InvalidOperationException("Native template part missing: " + name);
            var bounds = part.TransformToVisual(control).TransformBounds(
                new Windows.Foundation.Rect(0, 0, part.ActualWidth, part.ActualHeight));
            if (bounds.Y < -0.5 || bounds.Bottom > control.ActualHeight + 0.5)
                throw new InvalidOperationException($"Native {control.GetType().Name} clips {name}: part={bounds.Y:0.##}..{bounds.Bottom:0.##}, control height={control.ActualHeight:0.##}.");
        }
    }
    private static async Task<ReactorWindow> OnUIWindow(Func<ReactorWindow> open)
    {
        ReactorWindow? result = null;
        await OnUI(() => result = open());
        return result ?? throw new InvalidOperationException("Window was not created.");
    }
    private static Task OnUIAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AppSession.Post(async () =>
        {
            try { await action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }
    private static async Task CaptureAsync(ReactorWindow window, string path)
    {
        var image = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await image.RenderAsync(window.NativeWindow.Content);
        var buffer = await image.GetPixelsAsync();
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)image.PixelWidth, (uint)image.PixelHeight, 96, 96, buffer.ToArray());
        await encoder.FlushAsync();
        stream.Seek(0);
        using var source = stream.AsStreamForRead();
        await using var target = File.Create(path);
        await source.CopyToAsync(target);
    }
    private static DependencyObject? Find(DependencyObject node, string id)
    {
        if (AutomationProperties.GetAutomationId(node) == id) return node;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (Find(VisualTreeHelper.GetChild(node, i), id) is { } result) return result;
        return null;
    }
    private static void Invoke(ReactorWindow window, string id)
    {
        var button = Find(window.NativeWindow.Content, id) as Button ?? throw new InvalidOperationException($"Missing {id} button.");
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }
}
