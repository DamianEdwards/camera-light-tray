using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CameraLightTray.App.Platform;

internal static class SensorMonitorRecovery
{
    internal static bool IsExpected(Exception ex) => ex is COMException or Win32Exception or UnauthorizedAccessException or
        IOException or TimeoutException or DllNotFoundException or EntryPointNotFoundException;
    internal static async Task RunAsync(Func<CancellationToken, Task> observe, Action<Exception> failed,
        TimeSpan retryDelay, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try { await observe(token); }
                catch (Exception ex) when (IsExpected(ex))
                {
                    failed(ex);
                    await Task.Delay(retryDelay, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
