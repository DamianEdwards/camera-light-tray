namespace CameraLightTray.App.Native;

internal sealed class TrayFlyoutInteraction
{
    private bool _selectionPending, _awaitingMouseDown, _dismissedByTrayPress;
    internal bool SelectionPending => _selectionPending;

    internal void MouseDown()
    {
        if (!_awaitingMouseDown) _dismissedByTrayPress = false;
        _awaitingMouseDown = false;
        _selectionPending = true;
    }

    internal void MouseUp()
    {
        _awaitingMouseDown = false;
    }

    internal void LostActivation(bool pressedOnTray)
    {
        _dismissedByTrayPress = pressedOnTray;
        _awaitingMouseDown = pressedOnTray && !_selectionPending;
    }

    internal bool ShouldShow(bool visible, bool foreground, bool mouseSelection)
    {
        // The taskbar can take activation before NIN_SELECT arrives on mouse release.
        bool dismissCurrentClick = mouseSelection && _dismissedByTrayPress;
        Reset();
        return !dismissCurrentClick && (!visible || !foreground);
    }

    internal void Reset() => _selectionPending = _awaitingMouseDown = _dismissedByTrayPress = false;
}
