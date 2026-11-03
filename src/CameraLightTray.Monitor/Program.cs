using System.ServiceProcess;
using System.Runtime.InteropServices;
using System.Diagnostics;
using CameraLightTray.Core;
using Windows.Win32.System.Diagnostics.Etw;

namespace CameraLightTray.Monitor;

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args is ["--check"])
        {
            static void Size<T>(int expected) where T : struct
            {
                if (Marshal.SizeOf<T>() != expected) throw new InvalidOperationException($"Unexpected {typeof(T).Name} ABI size.");
            }
            Size<EVENT_TRACE_PROPERTIES>(120);
            Size<EVENT_TRACE_LOGFILEW>(448);
            Size<EVENT_RECORD>(112);
            Size<ENABLE_TRACE_PARAMETERS>(48);
            Size<PROPERTY_DATA_DESCRIPTOR>(16);
            using var current = Process.GetCurrentProcess();
            if (ProcessCatalog.Read(current.Id) is not { StartTime: > 0, AppKey.Length: > 0, UserSid.Length: > 0 })
                throw new InvalidOperationException("Current process identity could not be resolved.");
            Console.WriteLine("CameraLightTray monitor; LocalService, fixed provider, read-only IPC. No service or ETW session started.");
            Console.WriteLine("PASS: x64 ETW ABI sizes and live current-process creation time, executable/package and user identity.");
            return;
        }
        ServiceBase.Run(new MonitorService());
    }
}

internal sealed class MonitorService : ServiceBase
{
    private readonly CancellationTokenSource _stop = new();
    private MonitorHost? _host;
    internal MonitorService()
    {
        ServiceName = "CameraLightTrayMonitor";
        CanHandlePowerEvent = true;
        AutoLog = false;
    }
    protected override void OnStart(string[] args)
    {
        _host = new MonitorHost();
        _host.Start(_stop.Token);
    }
    protected override void OnStop()
    {
        _stop.Cancel();
        _host?.Dispose();
    }
    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (powerStatus is PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeSuspend)
            _host?.Invalidate("System resumed; pre-resume camera state is unknown.");
        return true;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _stop.Cancel(); _host?.Dispose(); _stop.Dispose(); }
        base.Dispose(disposing);
    }
}
