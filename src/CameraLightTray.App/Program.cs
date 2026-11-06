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
                    window.Show();
                    if (!smoke) window.Activate();
                }
                ReactorWindow ShowFlyout()
                {
                    int height = Math.Min(730, 178 + Math.Max(1, session.Preferences.Lights.Count) * 224);
                    if (flyout is null)
                    {
                        flyout = ReactorApp.OpenWindow(new WindowSpec
                        {
                            Title = demo ? "Camera Light Tray - Studio demo" : "Camera Light Tray - Light controls",
                            Width = 400, Height = height, Style = WindowStyle.None,
                            ResizeMode = WindowResizeMode.NoResize, ShowInTaskbar = false, ShowInSwitcher = false,
                            CornerStyle = WindowCornerStyle.Rounded, ActivateOnOpen = !smoke
                        }, () => new Studio(session));
                        flyout.Closing += (_, e) => { if (!exiting) { e.Cancel = true; flyout.Hide(); } };
                        flyout.Deactivated += (_, _) => { if (!smoke && !demo) flyout.Hide(); };
                        flyout.NativeWindow.Content.KeyDown += (_, e) =>
                        {
                            if (e.Key == Windows.System.VirtualKey.Escape) { flyout.Hide(); e.Handled = true; }
                        };
                    }
                    tray!.Place(flyout, height);
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
                Invoke(window, "OpenGeneral");
            });
            await Task.Delay(250);
            await OnUI(() =>
            {
                if (Find(window.NativeWindow.Content, "SetupMonitor") is not Button { IsEnabled: false })
                    throw new InvalidOperationException("Setup must be disabled in synthetic smoke.");
                var detail = Find(window.NativeWindow.Content, "Detail:Experimental tools") as Expander ??
                    throw new InvalidOperationException("Experimental disclosure missing.");
                detail.IsExpanded = false;
            });
            await Task.Delay(600);
            await OnUI(() =>
            {
                if (Find(window.NativeWindow.Content, "Detail:Experimental tools") is not Expander { IsExpanded: false })
                    throw new InvalidOperationException("Disclosure state must survive periodic UI updates.");
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
                File.WriteAllText(Path.Combine(directory, "smoke-result.txt"),
                    "PASS: Native Studio flyout and settings, custom tray callback, close/reopen, safe defaults, camera activation/restoration, manual brightness and power controls, settings cog, rules and disabled setup.");
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
