using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using CameraLightTray.Core;

namespace CameraLightTray.App.Platform;

[SupportedOSPlatform("windows")]
internal sealed partial class SensorActivityMonitor : IDisposable
{
    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(uint version, uint flags);
    [LibraryImport("mfplat.dll")]
    private static partial int MFShutdown();
    [LibraryImport("mfsensorgroup.dll")]
    private static partial int MFCreateSensorActivityMonitor(ISensorCallback callback, out ISensorMonitor monitor);

    private readonly Callback _callback;
    private ISensorMonitor? _monitor;
    private bool _started, _running;

    internal SensorActivityMonitor(Action<SensorActivity[]> report, Action<string> error) =>
        _callback = new(report, error);

    internal void Start()
    {
        Marshal.ThrowExceptionForHR(MFStartup(0x20070, 1));
        _started = true;
        try
        {
            Marshal.ThrowExceptionForHR(MFCreateSensorActivityMonitor(_callback, out var monitor));
            _monitor = monitor;
            monitor.Start();
            _running = true;
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        try
        {
            if (_monitor is { } monitor)
            {
                _monitor = null;
                try { if (_running) { _running = false; monitor.Stop(); } }
                finally
                {
                    try { ((ISensorShutdown)monitor).Shutdown(); }
                    finally { Release(monitor); }
                }
            }
        }
        finally
        {
            if (_started) { _started = false; Marshal.ThrowExceptionForHR(MFShutdown()); }
        }
    }

    private static void Release(object value)
    {
        if (value is ComObject instance) instance.FinalRelease();
    }

    [GeneratedComClass]
    private sealed partial class Callback(Action<SensorActivity[]> report, Action<string> error) : ISensorCallback
    {
        public unsafe void OnActivitiesReport(ISensorReports reports)
        {
            try
            {
                reports.GetCount(out uint count);
                if (count > 256) throw new InvalidDataException("Too many camera activity reports.");
                var result = new List<SensorActivity>();
                for (uint i = 0; i < count; i++)
                {
                    reports.GetActivityReport(i, out var activity);
                    try
                    {
                        char[] id = new char[32768], name = new char[1024];
                        fixed (char* text = id) activity.GetSymbolicLink(text, (uint)id.Length, out _);
                        fixed (char* text = name) activity.GetFriendlyName(text, (uint)name.Length, out _);
                        activity.GetProcessCount(out uint processes);
                        if (processes > 8192) throw new InvalidDataException("Too many camera client processes.");
                        var clients = new List<SensorProcess>();
                        for (uint p = 0; p < processes; p++)
                        {
                            activity.GetProcessActivity(p, out var client);
                            try
                            {
                                client.GetProcessId(out uint pid);
                                client.GetStreamingState(out int streaming);
                                client.GetReportTime(out long time);
                                clients.Add(new(checked((int)pid), streaming != 0, time));
                            }
                            finally { Release(client); }
                        }
                        result.Add(new(new string(id, 0, Array.IndexOf(id, '\0')),
                            new string(name, 0, Array.IndexOf(name, '\0')), clients.ToArray()));
                    }
                    finally { Release(activity); }
                }
                report(result.ToArray());
            }
            catch (Exception ex) { error("Windows sensor activity report failed: " + ex.Message); }
        }
    }
}

[GeneratedComInterface, Guid("D0CEF145-B3F4-4340-A2E5-7A5080CA05CB")]
internal partial interface ISensorMonitor
{
    void Start();
    void Stop();
}

[GeneratedComInterface, Guid("DE5072EE-DBE3-46DC-8A87-B6F631194751")]
internal partial interface ISensorCallback
{
    void OnActivitiesReport(ISensorReports reports);
}

[GeneratedComInterface, Guid("683F7A5E-4A19-43CD-B1A9-DBF4AB3F7777")]
internal partial interface ISensorReports
{
    void GetCount(out uint count);
    void GetActivityReport(uint index, out ISensorReport report);
    unsafe void GetActivityReportByDeviceName(char* name, out ISensorReport report);
}

[GeneratedComInterface, Guid("3E8C4BE1-A8C2-4528-90DE-2851BDE5FEAD")]
internal partial interface ISensorReport
{
    unsafe void GetFriendlyName(char* name, uint capacity, out uint written);
    unsafe void GetSymbolicLink(char* name, uint capacity, out uint written);
    void GetProcessCount(out uint count);
    void GetProcessActivity(uint index, out ISensorProcess process);
}

[GeneratedComInterface, Guid("39DC7F4A-B141-4719-813C-A7F46162A2B8")]
internal partial interface ISensorProcess
{
    void GetProcessId(out uint pid);
    void GetStreamingState(out int streaming);
    void GetStreamingMode(out int mode);
    void GetReportTime(out long time);
}

[GeneratedComInterface, Guid("97EC2EA4-0E42-4937-97AC-9D6D328824E1")]
internal partial interface ISensorShutdown
{
    void Shutdown();
    void GetShutdownStatus(out int status);
}
