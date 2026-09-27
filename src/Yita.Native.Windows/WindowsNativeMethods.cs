using System.Runtime.InteropServices;
using System.Text;
using Yita.Core.Selection;

namespace Yita.Native.Windows;

internal static class WindowsNativeMethods
{
    internal const int WhMouseLl = 14;
    internal const uint PmNoRemove = 0;
    internal const int WmQuit = 0x12;
    internal const int WmHotKey = 0x312;
    internal const int WmLButtonDown = 0x201;
    internal const int WmLButtonUp = 0x202;
    internal const uint ModControl = 0x2;
    internal const uint ModShift = 0x4;
    internal const uint ModNoRepeat = 0x4000;
    internal const ushort VirtualKeyT = 0x54;
    internal const ushort VirtualKeyC = 0x43;
    internal const ushort VirtualKeyControl = 0x11;
    internal const uint CfUnicodeText = 13;
    internal const uint GmemMoveable = 0x2;
    internal const uint GaRoot = 2;
    internal const uint GaRootOwner = 3;
    internal const uint LlMhfInjected = 0x1;
    internal const int SmCxDrag = 68;
    internal const int SmCyDrag = 69;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal IntPtr Hwnd;
        internal uint Value;
        internal UIntPtr WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal Point Point;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;

        internal readonly ScreenPoint ToScreenPoint() => new(X, Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input { internal uint Type; internal InputData Data; }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputData
    {
        [FieldOffset(0)] internal KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort Scan;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr Extra;
    }

    internal static Input KeyInput(ushort key, bool up = false) => new()
    {
        Type = 1,
        Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0u } },
    };

    internal delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseHookData
    {
        internal Point Point;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, ushort virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessage(out Message message, IntPtr windowHandle, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(out Message message, IntPtr windowHandle, uint min, uint max, uint remove);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelMouseProc callback,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hookHandle, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    internal static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("user32.dll")]
    internal static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeoutMilliseconds,
        out UIntPtr result);

    internal static bool IsForegroundTargetReady() =>
        OperatingSystem.IsWindows() && GetForegroundWindow() != IntPtr.Zero;
}
