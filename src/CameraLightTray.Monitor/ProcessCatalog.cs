using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Runtime.Versioning;
using CameraLightTray.Core;
using Microsoft.Win32.SafeHandles;

namespace CameraLightTray.Monitor;

[SupportedOSPlatform("windows")]
internal static unsafe partial class ProcessCatalog
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, int inherit, uint processId);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    private static partial int QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* name, ref uint size);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetProcessTimes(SafeProcessHandle process, out long creation, out long exit,
        out long kernel, out long user);
    [LibraryImport("kernel32.dll", EntryPoint = "GetPackageFamilyName", SetLastError = false)]
    private static partial int GetPackageFamilyName(SafeProcessHandle process, ref uint length, char* name);
    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSEnumerateProcessesExW", SetLastError = true)]
    private static partial int EnumerateWtsProcesses(nint server, ref uint level, uint session, out nint processes, out uint count);
    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSFreeMemoryExW", SetLastError = true)]
    private static partial int FreeWtsProcesses(int type, nint processes, uint count);
    [StructLayout(LayoutKind.Sequential)]
    private struct WtsProcess
    {
        internal uint Session, Pid;
        internal nint Name, Sid;
        internal uint Threads, Handles, Pagefile, PeakPagefile, WorkingSet, PeakWorkingSet;
        internal long UserTime, KernelTime;
    }

    internal static ProcessIdentity? Read(int pid, string? verifiedSid = null)
    {
        using var handle = OpenProcess(0x1000, 0, (uint)pid);
        if (handle.IsInvalid) return null;
        var buffer = new char[32768]; uint size = (uint)buffer.Length;
        fixed (char* name = buffer)
            if (QueryFullProcessImageName(handle, 0, name, ref size) == 0) return null;
        if (GetProcessTimes(handle, out long start, out _, out _, out _) == 0) return null;
        if (verifiedSid is null)
        {
            if (OpenProcessToken(handle, 8, out var token) == 0) return null;
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                verifiedSid = identity.User?.Value;
            if (verifiedSid is null) return null;
        }
        using (var process = Process.GetProcessById(pid))
        {
            string path = new(buffer, 0, (int)size);
            uint packageLength = 256;
            char* package = stackalloc char[(int)packageLength];
            string key = GetPackageFamilyName(handle, ref packageLength, package) == 0 ?
                "package:" + new string(package) + "|" + Path.GetFileName(path) : path;
            string name = Path.GetFileNameWithoutExtension(path);
            try { name = FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } friendly ? friendly : name; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return new(pid, start, process.SessionId, verifiedSid,
                key, name);
        }
    }

    // Called only inside the authenticated client's impersonation scope. WTS supplies
    // owner SIDs without requesting TOKEN_QUERY on another account's process.
    internal static (ProcessIdentity[] Processes, bool Uncertain) Enumerate(string userSid, int session)
    {
        var processes = new List<ProcessIdentity>();
        bool uncertain = false;
        long enumerationStarted = DateTimeOffset.UtcNow.ToFileTime();
        uint level = 1;
        if (EnumerateWtsProcesses(0, ref level, (uint)session, out nint memory, out uint count) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot enumerate client-session process owners.");
        try
        {
            var entries = new ReadOnlySpan<WtsProcess>((void*)memory, checked((int)count));
            foreach (var entry in entries)
            {
                if (entry.Pid == 0 || entry.Session != session) continue;
                if (entry.Sid == 0) { uncertain = true; continue; }
                if (new SecurityIdentifier(entry.Sid).Value != userSid) continue;
                try
                {
                    var identity = Read((int)entry.Pid, userSid);
                    // A PID created after enumeration began might have replaced
                    // the process whose owner SID WTS returned.
                    if (identity is not null && identity.StartTime <= enumerationStarted) processes.Add(identity);
                    else uncertain = true;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                    System.ComponentModel.Win32Exception)
                {
                    uncertain = true; // Retry next heartbeat; never infer absence from an incomplete read.
                }
            }
        }
        finally
        {
            if (FreeWtsProcesses(1, memory, count) == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot free client-session process enumeration.");
        }
        return (processes.ToArray(), uncertain);
    }
}
