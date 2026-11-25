using CameraLightTray.Core;

namespace CameraLightTray.App.Platform;

internal static class PreviewTrigger
{
    internal const string Key = "builtin:camera-light-tray-preview";
    internal const string Name = "Camera Light Tray preview";

    internal static Settings Include(Settings settings) => settings with
    {
        Apps = [.. settings.Apps.Where(app => !app.Key.Equals(Key, StringComparison.OrdinalIgnoreCase)), new(Key, Name, true)]
    };

    internal static ProcessIdentity[] Attribute(ProcessIdentity[] processes, int currentPid) =>
        processes.Select(process => process.Pid == currentPid ?
            process with { AppKey = Key, Name = Name } : process).ToArray();
}
