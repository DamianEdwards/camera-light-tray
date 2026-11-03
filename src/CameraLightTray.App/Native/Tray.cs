using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CameraLightTray.App.Native;

internal sealed unsafe partial class Tray : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        internal uint Size, Style;
        internal delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background;
        internal char* Menu;
        internal char* Name;
        internal nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IconData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id, Flags, Callback;
        internal nint Icon;
        internal fixed char Tip[128];
        internal uint State, StateMask;
        internal fixed char Info[256];
        internal uint Version;
        internal fixed char Title[64];
        internal uint InfoFlags;
        internal Guid Guid;
        internal nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X, Y; }
    private static readonly Dictionary<nint, Tray> Windows = [];
    private readonly nint _window;
    private readonly uint _taskbarCreated;
    private readonly Action _show, _pause, _exit;
    private readonly Action<string> _error;
    internal Action? Resumed;
    private bool _added;
    internal nint Handle => _window;
    internal void SelectForSmoke() => SendMessage(_window, 0x8002, 0, (nint)((1 << 16) | 0x400));
    internal static void CloseForSmoke(nint window) => SendMessage(window, 0x10, 0, 0);

    internal Tray(Action show, Action pause, Action exit, Action<string> error)
    {
        _show = show; _pause = pause; _exit = exit; _error = error;
        const string className = "CameraLightTray.Hidden";
        fixed (char* name = className)
        {
            var value = new WindowClass { Size = (uint)sizeof(WindowClass), Procedure = &Procedure,
                Instance = GetModuleHandle(null), Name = name };
            if (RegisterClassEx(ref value) == 0 && Marshal.GetLastPInvokeError() != 1410)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot register tray window.");
        }
        _window = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, 0, 0, GetModuleHandle(null), 0);
        if (_window == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot create tray window.");
        Windows.Add(_window, this);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        Add();
    }
    private IconData Data()
    {
        var data = new IconData { Size = (uint)sizeof(IconData), Window = _window, Id = 1,
            Flags = 1 | 2 | 4 | 0x80, Callback = 0x8002, Icon = LoadIcon(0, (nint)32516) };
        const string tip = "Camera Light Tray - open settings";
        tip.AsSpan().CopyTo(new Span<char>(data.Tip, 128));
        return data;
    }
    private void Add()
    {
        var data = Data();
        if (ShellNotifyIcon(0, ref data) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot add tray icon.");
        _added = true;
        data.Version = 4;
        if (ShellNotifyIcon(4, ref data) == 0) _error("Shell rejected tray keyboard support.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Procedure(nint window, uint message, nuint wparam, nint lparam)
    {
        if (Windows.TryGetValue(window, out var tray))
        {
            try
            {
                if (message == tray._taskbarCreated) { tray.Add(); return 0; }
                if (message == 0x218 && wparam is 7 or 18) { tray.Resumed?.Invoke(); return 1; }
                if (message == 0x11) return 1;
                if (message == 0x16 && wparam != 0) { tray._exit(); return 0; }
                if (message == 0x8002)
                {
                    int action = (int)((nuint)lparam & 0xFFFF);
                    if (action is 0x400 or 0x401) tray._show();
                    else if (action == 0x7B) tray.Menu();
                    return 0;
                }
            }
            catch (Exception ex) { tray._error("Tray operation failed: " + ex.Message); }
        }
        return DefWindowProc(window, message, wparam, lparam);
    }
    private void Menu()
    {
        using var menu = new MenuHandle(CreatePopupMenu());
        if (menu.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (AppendMenu(menu, 0, 1, "Open Camera Light Tray") == 0 ||
            AppendMenu(menu, 0, 2, "Pause / resume automation") == 0 ||
            AppendMenu(menu, 0, 3, "Exit") == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        GetCursorPos(out var point);
        SetForegroundWindow(_window);
        int command = TrackPopupMenuEx(menu, 0x102, point.X, point.Y, _window, 0);
        PostMessage(_window, 0, 0, 0);
        if (command == 1) _show();
        else if (command == 2) _pause();
        else if (command == 3) _exit();
    }
    public void Dispose()
    {
        if (_added) { var data = Data(); ShellNotifyIcon(2, ref data); _added = false; }
        Windows.Remove(_window);
        DestroyWindow(_window);
    }
    private sealed class MenuHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public MenuHandle() : base(true) { }
        internal MenuHandle(nint value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => DestroyMenu(handle) != 0;
    }
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? name);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassEx(ref WindowClass value);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(uint exStyle, string name, string title, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint window, uint message, nuint wparam, nint lparam);
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string message);
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    private static partial int ShellNotifyIcon(uint operation, ref IconData data);
    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    private static partial nint LoadIcon(nint instance, nint name);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int AppendMenu(MenuHandle menu, uint flags, nuint id, string text);
    [LibraryImport("user32.dll")]
    private static partial int DestroyMenu(nint menu);
    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(MenuHandle menu, uint flags, int x, int y, nint window, nint parameters);
    [LibraryImport("user32.dll")]
    private static partial int GetCursorPos(out Point point);
    [LibraryImport("user32.dll")]
    private static partial int SetForegroundWindow(nint window);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    private static partial int PostMessage(nint window, uint message, nuint wparam, nint lparam);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nuint wparam, nint lparam);
    [LibraryImport("user32.dll")]
    internal static partial int IsWindowVisible(nint window);
    [LibraryImport("user32.dll")]
    private static partial int DestroyWindow(nint window);
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBox(nint window, string text, string caption, uint flags);
}
