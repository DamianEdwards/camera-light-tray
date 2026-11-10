namespace CameraLightTray.App.Diagnostics;

internal static class LogIdentity
{
    internal static string Token(string value) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..12];
}
