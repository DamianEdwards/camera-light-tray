using CameraLightTray.App.Native;

namespace CameraLightTray.Tests;

internal static class TrayFlyoutChecks
{
    internal static int Run()
    {
        int passed = 0;
        static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        void Test(string name, Action test) { test(); Console.WriteLine("PASS " + name); passed++; }

        Test("tray selection opens hidden popups, hides foreground popups, and recovers background popups", () =>
        {
            var popup = new TrayFlyoutInteraction();
            Assert(popup.ShouldShow(false, false, true), "Closed popup was not opened.");
            Assert(!popup.ShouldShow(true, true, true), "Open foreground popup was not toggled off.");
            Assert(popup.ShouldShow(true, false, true), "Background popup was treated as already open in the foreground.");
        });
        Test("tray-click deactivation after mouse down is consumed without reopening the dismissed popup", () =>
        {
            var popup = new TrayFlyoutInteraction();
            popup.MouseDown();
            popup.LostActivation(pressedOnTray: true);
            popup.MouseUp();
            Assert(!popup.ShouldShow(false, false, true), "NIN_SELECT reopened the popup hidden by the same tray press.");
            popup.MouseDown();
            popup.MouseUp();
            Assert(popup.ShouldShow(false, false, true), "Following tray click did not reopen the popup.");
        });
        Test("tray-click deactivation before mouse down has the same toggle behavior", () =>
        {
            var popup = new TrayFlyoutInteraction();
            popup.LostActivation(pressedOnTray: true);
            popup.MouseDown();
            popup.MouseUp();
            Assert(!popup.ShouldShow(false, false, true), "Late mouse-down delivery discarded the pending dismissal.");
        });
        Test("canceled tray presses cannot suppress a subsequent new click", () =>
        {
            var popup = new TrayFlyoutInteraction();
            popup.MouseDown();
            popup.LostActivation(pressedOnTray: true);
            // The first press was canceled by releasing outside the tray icon.
            popup.MouseDown();
            popup.MouseUp();
            Assert(popup.ShouldShow(false, false, true), "Canceled press suppressed the next click.");
        });
        Test("ordinary focus loss and keyboard selection do not inherit mouse toggle suppression", () =>
        {
            var popup = new TrayFlyoutInteraction();
            popup.LostActivation(pressedOnTray: false);
            Assert(popup.ShouldShow(false, false, true), "Focus loss suppressed reopening.");
            popup.MouseDown();
            popup.LostActivation(pressedOnTray: true);
            Assert(popup.ShouldShow(false, false, false), "Keyboard activation inherited a mouse dismissal.");
            Assert(!popup.ShouldShow(true, true, false), "Keyboard selection did not toggle an active popup.");
        });
        Test("explicit dismissals clear stale input state", () =>
        {
            var popup = new TrayFlyoutInteraction();
            popup.LostActivation(pressedOnTray: true);
            popup.Reset();
            popup.MouseDown();
            popup.MouseUp();
            Assert(popup.ShouldShow(false, false, true), "Escape or settings dismissal left a pending tray toggle.");
        });
        return passed;
    }
}
