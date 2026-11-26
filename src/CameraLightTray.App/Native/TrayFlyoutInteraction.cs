namespace CameraLightTray.App.Native;

internal sealed class TrayFlyoutInteraction
{
    private bool _mouseDown, _awaitingMouseDown, _dismissedByTrayPress;

    internal void MouseDown()
    {
        if (!_awaitingMouseDown) _dismissedByTrayPress = false;
        _awaitingMouseDown = false;
        _mouseDown = true;
    }

    internal void MouseUp()
    {
        _mouseDown = _awaitingMouseDown = false;
    }

    internal void LostActivation(bool pressedOnTray)
    {
        _dismissedByTrayPress = pressedOnTray;
        _awaitingMouseDown = pressedOnTray && !_mouseDown;
    }

    internal bool ShouldShow(bool visible, bool foreground, bool mouseSelection)
    {
        // The taskbar can take activation before NIN_SELECT arrives on mouse release.
        bool dismissCurrentClick = mouseSelection && _dismissedByTrayPress;
        Reset();
        return !dismissCurrentClick && (!visible || !foreground);
    }

    internal void Reset() => _mouseDown = _awaitingMouseDown = _dismissedByTrayPress = false;
}
