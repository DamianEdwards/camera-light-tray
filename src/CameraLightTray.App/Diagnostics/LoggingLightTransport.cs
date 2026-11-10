using CameraLightTray.Core;
using Microsoft.Extensions.Logging;

namespace CameraLightTray.App.Diagnostics;

internal sealed class LoggingLightTransport(ILightTransport inner, ILogger logger) : ILightTransport
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (LightState? State, string? Error)> _last = [];

    public async Task<LightState> ReadAsync(LightDevice device, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope("light={LightToken}", LogIdentity.Token(device.Id));
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var state = await inner.ReadAsync(device, cancellationToken);
            lock (_gate)
            {
                _last.TryGetValue(device.Id, out var previous);
                if (previous.State != state || previous.Error is not null)
                    logger.LogInformation("Light readback {Light}; on={On}, brightness={Brightness}, temperature={Temperature}, recovered={Recovered}, elapsedMs={ElapsedMs}",
                        device.Name, state.On, state.Brightness, state.Temperature, previous.Error is not null, started.ElapsedMilliseconds);
                _last[device.Id] = (state, null);
            }
            return state;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _last.TryGetValue(device.Id, out var previous);
                if (previous.Error != ex.Message)
                    logger.LogWarning(ex, "Light readback failed {Light}; elapsedMs={ElapsedMs}", device.Name, started.ElapsedMilliseconds);
                _last[device.Id] = (previous.State, ex.Message);
            }
            throw;
        }
    }

    public async Task WriteAsync(LightDevice device, LightState state, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope("light={LightToken}", LogIdentity.Token(device.Id));
        logger.LogInformation("Light write requested {Light}; on={On}, brightness={Brightness}, temperature={Temperature}",
            device.Name, state.On, state.Brightness, state.Temperature);
        try
        {
            await inner.WriteAsync(device, state, cancellationToken);
            logger.LogInformation("Light write acknowledged {Light}", device.Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogError(ex, "Light write failed {Light}; readback/recovery remains authoritative", device.Name); throw; }
    }
}
