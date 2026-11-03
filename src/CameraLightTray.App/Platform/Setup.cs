using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace CameraLightTray.App.Platform;

internal static class Setup
{
    internal static async Task InstallAsync()
    {
        string script = Path.Combine(AppContext.BaseDirectory, "setup-monitor.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("Use the published ZIP (including setup-monitor.ps1 and monitor payload) for setup.");
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = true, Verb = "runas",
            Arguments = $"-NoProfile -File \"{script}\"",
            WorkingDirectory = AppContext.BaseDirectory
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Setup did not start.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Win32Exception(process.ExitCode, "Monitor setup failed or was cancelled. No automation enabled.");
    }
    internal static void Startup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (enabled)
            key.SetValue("CameraLightTray", $"\"{Environment.ProcessPath}\" --startup", RegistryValueKind.String);
        else key.DeleteValue("CameraLightTray", false);
    }
}
