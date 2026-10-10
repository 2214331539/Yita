using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Yita.Core.Selection;

namespace Yita.Native.Windows;

public sealed class WindowsTrayIcon : Yita.Core.Platform.IStatusIcon
{
    private const uint CallbackMessage = 0x8001;
    private readonly string _iconPath;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WindowProc _procedure;
    private readonly Thread _thread;
    private IntPtr _window;
    private IntPtr _icon;
    private uint _threadId;
    private uint _taskbarCreated;
    private Exception? _error;
    private bool _disposed;

    public event EventHandler<ScreenPoint>? MenuRequested;
    public event EventHandler? OpenRequested;
    public bool IsRunning => !_disposed && _error is null && _thread.IsAlive && _window != IntPtr.Zero;

    public WindowsTrayIcon(string iconPath)
    {
        _iconPath = iconPath;
        _procedure = HandleMessage;
        _thread = new Thread(Run) { IsBackground = true, Name = "Yita.Avalonia.Tray" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(3))) { Dispose(); throw new TimeoutException("Tray startup timed out."); }
        if (_error is not null) { Dispose(); throw new InvalidOperationException("Tray startup failed.", _error); }
    }

    private void Run()
    {
        var className = "Yita.Avalonia.Tray." + Environment.ProcessId;
        try
        {
            _threadId = WindowsNativeMethods.GetCurrentThreadId();
            var windowClass = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = _procedure,
                Instance = WindowsNativeMethods.GetModuleHandle(null), ClassName = className };
            if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _window = CreateWindowEx(0, className, "Yita", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, windowClass.Instance, IntPtr.Zero);
            if (_window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _icon = LoadImage(IntPtr.Zero, _iconPath, 1, 0, 0, 0x10 | 0x40);
            if (_icon == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            AddIcon();
        }
        catch (Exception exception) { _error = exception; }
        finally { _ready.Set(); }

        try
        {
            if (_error is null)
                while (WindowsNativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                { TranslateMessage(ref message); DispatchMessage(ref message); }
        }
        finally
        {
            var data = Data();
            ShellNotifyIcon(2, ref data);
            if (_icon != IntPtr.Zero) DestroyIcon(_icon);
            if (_window != IntPtr.Zero) DestroyWindow(_window);
            UnregisterClass(className, WindowsNativeMethods.GetModuleHandle(null));
            _threadId = 0;
        }
    }

    private void AddIcon()
    {
        var data = Data();
        if (!ShellNotifyIcon(0, ref data)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = 1,
        Flags = 1 | 2 | 4, Callback = CallbackMessage, Icon = _icon, Tip = "Yita", Info = "", Title = "",
    };

    private IntPtr HandleMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (_disposed) return DefWindowProc(window, message, wParam, lParam);
            if (message == _taskbarCreated && _taskbarCreated != 0) AddIcon();
            else if (message == CallbackMessage)
            {
                var mouseMessage = unchecked((uint)lParam.ToInt64());
                if (mouseMessage is 0x205 or 0x7B && WindowsNativeMethods.GetCursorPos(out var point))
                    MenuRequested?.Invoke(this, new ScreenPoint(point.X, point.Y));
                else if (mouseMessage == 0x203) OpenRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch { }
        return DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) WindowsNativeMethods.PostThreadMessage(_threadId, WindowsNativeMethods.WmQuit, UIntPtr.Zero, IntPtr.Zero);
        if (!ReferenceEquals(Thread.CurrentThread, _thread)) _thread.Join(TimeSpan.FromSeconds(3));
        if (!_thread.IsAlive) _ready.Dispose();
    }

    private delegate IntPtr WindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style; public WindowProc Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background; public string? MenuName, ClassName; public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref WindowsNativeMethods.Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref WindowsNativeMethods.Message message);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
}
