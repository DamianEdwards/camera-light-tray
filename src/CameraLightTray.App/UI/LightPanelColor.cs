using CameraLightTray.Core;

namespace CameraLightTray.App.UI;

internal static class LightPanelColor
{
    internal static string FromReadback(LightState state)
    {
        double kelvin = LightControls.Kelvin(state.Temperature);
        double blend = kelvin <= 4900 ? (kelvin - 2900) / 2000 : (kelvin - 4900) / 2100;
        var low = kelvin <= 4900 ? (R: 255, G: 195, B: 128) : (R: 255, G: 241, B: 219);
        var high = kelvin <= 4900 ? (R: 255, G: 241, B: 219) : (R: 213, G: 232, B: 255);
        double brightness = Math.Sqrt(Math.Clamp(state.Brightness, 0, 100) / 100d);
        int Channel(int dark, int start, int end) =>
            (int)Math.Round(dark + (start + (end - start) * Math.Clamp(blend, 0, 1) - dark) * brightness);
        return $"#{Channel(42, low.R, high.R):X2}{Channel(49, low.G, high.G):X2}{Channel(56, low.B, high.B):X2}";
    }
}
