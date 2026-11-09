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
using System.Runtime.InteropServices.WindowsRuntime;

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
        Directory.CreateDirectory(directory);
        ReactorWindow? window = null;
        ReactorWindow? flyout = null;
        Tray? tray = null;
        AppSession? session = null;
        bool exiting = false;
        int exitCode = 0;
        try
        {
            using var instance = new FileStream(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            session = new(new(directory), isolated, demo);
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
                ReactorWindow ShowFlyout()
                {
                    if (flyout is null)
                    {
                        flyout = ReactorApp.OpenWindow(new WindowSpec
                        {
                            Title = demo ? "Camera Light Tray - Studio demo" : "Camera Light Tray - Light controls",
                            Width = 400, Style = WindowStyle.None,
                            SizeToContent = WindowSizeToContent.Height, MaxHeight = 730,
                            ResizeMode = WindowResizeMode.NoResize, ShowInTaskbar = false, ShowInSwitcher = false,
                            CornerStyle = WindowCornerStyle.Rounded, ActivateOnOpen = !smoke
                        }, () => new Studio(session));
                        flyout.Closing += (_, e) => { if (!exiting) { e.Cancel = true; flyout.Hide(); } };
                        flyout.Deactivated += (_, _) => { if (!smoke && !demo) flyout.Hide(); };
                        flyout.NativeWindow.SizeChanged += (_, _) => { if (!exiting) tray?.Place(flyout); };
                        flyout.NativeWindow.Content.KeyDown += (_, e) =>
                        {
                            if (e.Key == Windows.System.VirtualKey.Escape) { flyout.Hide(); e.Handled = true; }
                        };
                    }
                    tray!.Place(flyout);
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
                        File.WriteAllText(Path.Combine(directory, "shutdown-error.txt"), ex.Message);
                        exitCode = 1;
                        if (!smoke) Tray.MessageBox(0, "Some owned light states could not be restored. Recovery is retained for next launch.\n" + ex.Message,
                            "Camera Light Tray", 0x30);
                    }
                    finally { tray?.Dispose(); tray = null; flyout?.Close(); window?.Close(); ReactorApp.Exit(exitCode); }
                }
                Application.Current.UnhandledException += (_, e) =>
                {
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
                session.Start();
                if (smoke || args.Contains("--settings")) ShowSettings();
                else if (!args.Contains("--startup")) ShowFlyout();
                if (smoke) _ = SmokeAsync(window!, ShowFlyout, tray, session, directory, code => { exitCode = code; Exit(); });
            });
            return exitCode;
        }
        catch (Exception ex)
        {
            if (smoke) File.WriteAllText(Path.Combine(directory, "smoke-result.txt"), "FAIL: " + ex);
            else Tray.MessageBox(0, ex.Message, "Camera Light Tray could not start", 0x10);
            return 1;
        }
        finally { tray?.Dispose(); }
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
            var readbackWait = System.Diagnostics.Stopwatch.StartNew();
            while (session.Preferences.Lights.Any(light => session.Observation(light.Id).State?.On != 1))
            {
                if (readbackWait.Elapsed > TimeSpan.FromSeconds(10)) throw new InvalidOperationException("Activated demo lights did not receive on readbacks.");
                await Task.Delay(100);
            }
            await Task.Delay(300);
            await OnUI(() =>
            {
                var root = (FrameworkElement)lit.NativeWindow.Content;
                var last = Find(root, "Temperature-demo:air") as FrameworkElement ??
                    throw new InvalidOperationException("Second light's temperature control is missing.");
                var bounds = last.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, last.ActualWidth, last.ActualHeight));
                if (bounds.Bottom > root.ActualHeight - 30)
                    throw new InvalidOperationException("Both lights' sliders must fit in the Studio flyout without scrolling.");
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
            await OnUIAsync(() => CaptureAsync(lit, Path.Combine(directory, "studio-on.png")));
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
                Invoke(popup!, "OpenSettings");
            });
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
                    "PASS: Native Studio flyout and settings, unclipped native switch/slider template parts in normal and hover states, fully visible light rows, page-scroll reset with background preservation, shared appearance, stable navigation hover, camera automation, manual controls and safe setup defaults.");
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
            });
            if (mode != AppearanceMode.System)
            {
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
