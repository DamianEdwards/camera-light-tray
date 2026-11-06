namespace CameraLightTray.Core;

public enum LightModel { Unknown, KeyLight, KeyLightAir }

public static class LightControls
{
    public static LightModel Model(LightDevice light)
    {
        if (light.ProductName is not null)
            return light.ProductName switch
            {
                "Elgato Key Light" or "Key Light" => LightModel.KeyLight,
                "Elgato Key Light Air" or "Key Light Air" => LightModel.KeyLightAir,
                _ => LightModel.Unknown
            };
        string name = light.ProductName ?? light.Name;
        if (name.Contains("MK.2", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Neo", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Mini", StringComparison.OrdinalIgnoreCase)) return LightModel.Unknown;
        if (name.Contains("Key Light Air", StringComparison.OrdinalIgnoreCase)) return LightModel.KeyLightAir;
        if (name.Equals("Elgato Key Light", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Key Light", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Elgato Key Light ", StringComparison.OrdinalIgnoreCase)) return LightModel.KeyLight;
        return LightModel.Unknown;
    }

    public static int Kelvin(int temperature) => temperature is >= 100 and <= 500 ?
        (int)Math.Round(1000000d / temperature) : throw new ArgumentOutOfRangeException(nameof(temperature));

    public static int Temperature(int kelvin) => kelvin is >= 2900 and <= 7000 ?
        Math.Clamp((int)Math.Round(1000000d / kelvin), 143, 344) : throw new ArgumentOutOfRangeException(nameof(kelvin));

    public static LightState Adjust(LightState state, int? on = null, int? brightness = null, int? kelvin = null)
    {
        if (on is not null and not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(on));
        if (brightness is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(brightness));
        return state with { On = on ?? state.On, Brightness = brightness ?? state.Brightness,
            Temperature = kelvin is int value ? Temperature(value) : state.Temperature };
    }
}
