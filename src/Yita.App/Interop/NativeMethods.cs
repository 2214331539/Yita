using System.Runtime.InteropServices;
using System.Text;
using Yita.Models;

namespace Yita.Interop;

internal static class NativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    internal const int WhMouseLl = 14;
    internal const int WmQuit = 0x0012;
    internal const int WmMouseActivate = 0x0021;
    internal const int WmSetCursor = 0x0020;
    internal const int WmNcHitTest = 0x0084;
    internal const int WmGetText = 0x000D;
    internal const int WmGetTextLength = 0x000E;
    internal const int WmCopy = 0x0301;
    internal const int WmHotKey = 0x0312;
    internal const int WmLButtonDown = 0x0201;
    internal const int WmLButtonUp = 0x0202;
    internal const uint PmNoRemove = 0x0000;
    internal const int MaNoActivate = 3;
    internal const uint GaRoot = 2;
    internal const uint GaParent = 1;
    internal const uint GaRootOwner = 3;
    internal const uint EmGetSel = 0x00B0;
    internal const uint EmGetSelText = 0x00C2;
    internal const uint SmtoAbortIfHung = 0x0002;
    internal const int HtClient = 1;
    internal const int HtCaption = 2;
    internal const int HtSize = 4;
    internal const int HtHScroll = 6;
    internal const int HtVScroll = 7;
    internal const int HtLeft = 10;
    internal const int HtRight = 11;
    internal const int HtTop = 12;
    internal const int HtTopLeft = 13;
    internal const int HtTopRight = 14;
    internal const int HtBottom = 15;
    internal const int HtBottomLeft = 16;
    internal const int HtBottomRight = 17;
    internal const int HtBorder = 18;
    internal const int GwlStyle = -16;
    internal const int GwlExStyle = -20;
    internal const long EsPassword = 0x00000020L;
    internal const long WsExTransparent = 0x00000020L;
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExNoActivate = 0x08000000L;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;
    internal const uint LlMhfInjected = 0x00000001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModNoRepeat = 0x4000;
    internal const int SmCxDrag = 68;
    internal const int SmCyDrag = 69;
    internal const uint GrGdiObjects = 0;
    internal const uint GrUserObjects = 1;
    internal const uint CoInitMultithreaded = 0x0;
    internal const uint SpiGetScreenReader = 0x0046;
    internal const uint SpiSetScreenReader = 0x0047;
    internal const uint SpifSendChange = 0x0002;
    internal static readonly IntPtr HwndTopmost = new(-1);
    internal static readonly IntPtr CursorSizeNesw = new(32643);
    internal static readonly IntPtr CursorSizeNwse = new(32642);
    internal static readonly IntPtr CursorSizeNs = new(32645);
    internal static readonly IntPtr CursorSizeWe = new(32644);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfoGet(
        uint action,
        uint parameter,
        ref int value,
        uint updateFlags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfoSet(
        uint action,
        uint parameter,
        IntPtr value,
        uint updateFlags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCursor(IntPtr cursor);

    internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
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

        internal readonly int Width => Right - Left;

        internal readonly int Height => Bottom - Top;

        internal readonly bool Contains(ScreenPoint point)
        {
            return point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseHookData
    {
        internal NativePoint Point;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal IntPtr Hwnd;
        internal uint Value;
        internal UIntPtr WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal NativePoint Point;
        internal uint Private;
    }

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

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessage(out Message message, IntPtr windowHandle, uint minFilter, uint maxFilter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(
        out Message message,
        IntPtr windowHandle,
        uint minFilter,
        uint maxFilter,
        uint removeMessage);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    internal static extern int CoEnableCallCancellation(IntPtr reserved);

    [DllImport("ole32.dll")]
    internal static extern int CoDisableCallCancellation(IntPtr reserved);

    [DllImport("ole32.dll")]
    internal static extern int CoCancelCall(uint threadId, uint timeoutSeconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    internal static extern uint GetGuiResources(IntPtr processHandle, uint flags);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(
        IntPtr windowHandle,
        StringBuilder className,
        int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr windowHandle,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeoutMilliseconds,
        out UIntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

}
