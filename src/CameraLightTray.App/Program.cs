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
            else if (args[i] is not ("--smoke-test" or "--demo" or "--isolated" or "--startup"))
            { Tray.MessageBox(0, "Unknown or incomplete argument: " + args[i], "Camera Light Tray", 0x10); return 1; }
        }
        if ((isolated || demo) && string.IsNullOrWhiteSpace(explicitDirectory))
        { Tray.MessageBox(0, "Isolated/demo/smoke runs require --data-dir <separate-directory>.", "Camera Light Tray", 0x10); return 1; }
        string directory = explicitDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CameraLightTray");
        Directory.CreateDirectory(directory);
        ReactorWindow? window = null;
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
                void Show()
                {
                    if (window is null)
                    {
                        window = ReactorApp.OpenWindow(new WindowSpec
                        {
                            Title = demo ? "Camera Light Tray - Synthetic demo" : "Camera Light Tray",
                            Width = 820, Height = 880, MinWidth = 620, MinHeight = 500,
                            ActivateOnOpen = !smoke, Backdrop = BackdropChoice.Of(BackdropKind.Mica)
                        }, () => new Dashboard(session));
                        window.Closing += (_, e) => { if (!exiting) { e.Cancel = true; window.Hide(); } };
                    }
                    window.Show();
                    if (!smoke) window.Activate();
                }
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
                    finally { tray?.Dispose(); tray = null; window?.Close(); ReactorApp.Exit(exitCode); }
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
                tray = new(Show, () => session.SetMode(session.Mode == ManualMode.Paused ? ManualMode.Automatic : ManualMode.Paused),
                    Exit, session.Report);
                tray.Resumed += session.Resume;
                session.Start();
                if (!args.Contains("--startup") || smoke) Show();
                if (smoke) _ = SmokeAsync(window!, tray, session, directory, code => { exitCode = code; Exit(); });
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

    private static async Task SmokeAsync(ReactorWindow window, Tray tray, AppSession session, string directory, Action<int> finish)
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
                Invoke(window, "DemoStop"); Invoke(window, "SaveRules");
            });
            await Task.Delay(3200);
            await OnUI(() =>
            {
                if (!session.AutomationDetail.Contains("restored", StringComparison.Ordinal))
                    throw new InvalidOperationException("Synthetic release did not restore prior state.");
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
            await OnUI(() =>
            {
                nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window.NativeWindow);
                if (Tray.IsWindowVisible(handle) == 0) throw new InvalidOperationException("Tray selection must reopen the UI. " + session.Error);
                File.WriteAllText(Path.Combine(directory, "smoke-result.txt"),
                    "PASS: Native Reactor UI, real tray callback, close-to-tray/reopen, safe defaults, toggle, camera activation, simulated light on/restoration, rules and disabled setup.");
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
