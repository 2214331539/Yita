using System.Runtime.InteropServices;
using Yita.Interop;

namespace Yita.Selection;

/// <summary>Sends one copy chord only to the still-focused, non-password target.</summary>
internal static class CopyShortcut
{
    internal static bool IsTargetCurrent(IntPtr target)
    {
        var root = NativeMethods.GetAncestor(target, NativeMethods.GaRootOwner);
        var foregroundRoot = NativeMethods.GetAncestor(NativeMethods.GetForegroundWindow(), NativeMethods.GaRootOwner);
        return root != IntPtr.Zero && root == foregroundRoot;
    }

    internal static IntPtr GetFocusedTarget(IntPtr target)
    {
        if (!IsTargetCurrent(target)) return IntPtr.Zero;
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        // Embedded WPS PDF canvases may belong to a different GUI thread from
        // the frame. Query the foreground input queue, not the hit-test thread.
        return GetGUIThreadInfo(0, ref info) && info.Focus != IntPtr.Zero
            && IsTargetCurrent(info.Focus) ? info.Focus : IntPtr.Zero;
    }

    internal static bool TrySend(IntPtr target)
    {
        if (!IsTargetCurrent(target)) return false;
        // Do not release keys held by the user, or turn Ctrl+C into another command.
        foreach (var key in new[] { 0x01, 0x02, 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x43 })
            if ((GetAsyncKeyState(key) & 0x8000) != 0) return false;

        var focus = GetFocusedTarget(target);
        if (focus == IntPtr.Zero
            || NativeSelectionReader.IsPasswordStyle(NativeMethods.GetWindowLongPtr(focus, NativeMethods.GwlStyle).ToInt64()))
            return false;

        var inputs = new[] { Key(0x11), Key(0x43), Key(0x43, up: true), Key(0x11, up: true) };
        if (!IsTargetCurrent(target)) return false;
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent > 0 && sent < inputs.Length)
        {
            // Release only the keys this batch may have pressed.
            var releases = sent >= 2 ? new[] { Key(0x43, true), Key(0x11, true) } : new[] { Key(0x11, true) };
            SendInput((uint)releases.Length, releases, Marshal.SizeOf<Input>());
        }
        return sent == inputs.Length;
    }

    private static Input Key(ushort key, bool up = false) => new()
    {
        Type = 1,
        Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0u } },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeMethods.NativeRect CaretRect;
    }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
