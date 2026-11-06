namespace CameraLightTray.App.Native;

internal readonly record struct PixelRect(int X, int Y, int Width, int Height);
internal static class FlyoutPlacement
{
    internal static PixelRect Fit(PixelRect work, PixelRect anchor, int width, int height, int gap)
    {
        width = Math.Min(width, work.Width);
        height = Math.Min(height, work.Height);
        int x = anchor.X + anchor.Width - width;
        int y = anchor.Y - height - gap;
        if (anchor.Y <= work.Y) y = anchor.Y + anchor.Height + gap;
        if (anchor.X >= work.X + work.Width) x = anchor.X - width - gap;
        if (anchor.X + anchor.Width <= work.X) x = anchor.X + anchor.Width + gap;
        return new(Math.Clamp(x, work.X, work.X + work.Width - width),
            Math.Clamp(y, work.Y, work.Y + work.Height - height), width, height);
    }
}
