namespace CameraLightTray.Core;

public static class AppPriority
{
    public static bool IsCameraOrCommunicationApp(string key) =>
        key[(key.LastIndexOfAny(['\\', '/', '|']) + 1)..].ToLowerInvariant() is
            "ms-teams.exe" or "teams.exe" or "signal.exe" or "zoom.exe" or "discord.exe" or
            "windowscamera.exe" or "skype.exe" or "webex.exe" or "obs64.exe" or "slack.exe" or "cameralighttray.exe";
}
