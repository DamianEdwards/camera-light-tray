using CameraLightTray.Core;

namespace CameraLightTray.App.Diagnostics;

internal sealed record ActivityStatus(string Code, string Title)
{
    internal static ActivityStatus Evaluate(Settings settings, MonitorSnapshot? snapshot, ManualMode mode, DateTimeOffset now)
    {
        if (!settings.AutomationEnabled) return new("automation-disabled", "Automation off");
        if (mode == ManualMode.Paused) return new("paused", "Automation paused");
        if (mode == ManualMode.ForceOn) return new("manual-on", "Manual on");
        if (mode == ManualMode.ForceOff) return new("manual-off", "Manual off");
        if (!settings.Apps.Any(a => a.Enabled)) return new("no-trigger-apps", "Choose trigger apps");
        if (snapshot is null) return new("snapshot-unavailable", "Camera state unknown");
        if (now - snapshot.Time >= TimeSpan.FromSeconds(4)) return new("snapshot-stale", "Camera state unknown");
        if (snapshot.Apps.Any(app => app.Status == CameraStatus.Active &&
            settings.Apps.Any(a => a.Enabled && a.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase))))
            return new("selected-camera-active", "Camera in use");
        return snapshot.Healthy ? new("healthy-idle", "Ready for your next call") :
            new("sensor-unhealthy", "Camera state unknown");
    }
}
