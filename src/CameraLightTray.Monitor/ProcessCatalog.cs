using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using CameraLightTray.Core;
using Microsoft.Win32.SafeHandles;

namespace CameraLightTray.Monitor;

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

    internal static ProcessIdentity? Read(int pid)
    {
        using var handle = OpenProcess(0x1000, 0, (uint)pid);
        if (handle.IsInvalid) return null;
        var buffer = new char[32768]; uint size = (uint)buffer.Length;
        fixed (char* name = buffer)
            if (QueryFullProcessImageName(handle, 0, name, ref size) == 0) return null;
        if (GetProcessTimes(handle, out long start, out _, out _, out _) == 0 ||
            OpenProcessToken(handle, 8, out var token) == 0) return null;
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
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
            return new(pid, start, process.SessionId, identity.User?.Value ?? "",
                key, name);
        }
    }

    internal static (ProcessIdentity[] Processes, HashSet<int> UncertainSessions) Enumerate()
    {
        var processes = new List<ProcessIdentity>();
        var uncertain = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == 0) continue;
                    var identity = Read(process.Id);
                    if (identity is not null) processes.Add(identity);
                    else uncertain.Add(process.SessionId);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                    System.ComponentModel.Win32Exception)
                {
                    // A process can exit between enumeration and inspection. No stale identity is retained.
                }
            }
        }
        return (processes.ToArray(), uncertain);
    }
}
