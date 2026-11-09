using CameraLightTray.Core;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace CameraLightTray.App.UI;

internal static class AppTheme
{
    private static readonly UISettings Settings = new();
    private static AppearanceMode _mode;
    private static Action? _changed;
    private static bool _systemDark;
    internal static bool Dark => _mode == AppearanceMode.Dark || _mode == AppearanceMode.System && _systemDark;
    internal static ElementTheme Element => Dark ? ElementTheme.Dark : ElementTheme.Light;

    internal static void Start(AppearanceMode mode, Action changed)
    {
        _mode = mode;
        _changed = changed;
        _systemDark = SystemDark();
        Settings.ColorValuesChanged += ColorsChanged;
    }
    internal static void Stop()
    {
        Settings.ColorValuesChanged -= ColorsChanged;
        _changed = null;
    }
    private static bool SystemDark()
    {
        var color = Settings.GetColorValue(UIColorType.Background);
        return color.R * 0.299 + color.G * 0.587 + color.B * 0.114 < 128;
    }
    private static void ColorsChanged(UISettings _, object args) => AppSession.Post(() =>
    {
        _systemDark = SystemDark();
        _changed?.Invoke();
    });
    internal static void Select(AppearanceMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        _changed?.Invoke();
    }
}
