using System.Runtime.InteropServices;

namespace Yita.Native.Windows;

// Capture every restorable format before sending copy. Unsupported native
// handles stop automatic copying so an existing rich clipboard stays intact.
internal sealed class WindowsClipboardSnapshot : IDisposable
{
    private const int MaximumBytes = 16 * 1024 * 1024;
    private readonly List<(uint Format, byte[]? Bytes, IntPtr Bitmap)> _formats = [];
    internal uint Sequence { get; private set; }

    internal static WindowsClipboardSnapshot? TryCapture()
    {
        if (!WindowsNativeMethods.OpenClipboard(IntPtr.Zero)) return null;
        var snapshot = new WindowsClipboardSnapshot();
        try
        {
            snapshot.Sequence = WindowsNativeMethods.GetClipboardSequenceNumber();
            var total = 0L;
            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                var handle = WindowsNativeMethods.GetClipboardData(format);
                if (handle == IntPtr.Zero) { snapshot.Dispose(); return null; }
                if (format == 2)
                {
                    var bitmap = CopyImage(handle, 0, 0, 0, 0x2000);
                    if (bitmap == IntPtr.Zero) { snapshot.Dispose(); return null; }
                    snapshot._formats.Add((format, null, bitmap));
                    continue;
                }
                // Metafile, palette, owner-display and private handles have
                // object-specific ownership and cannot be copied as HGLOBAL.
                if (format is 3 or 9 or 14 or 0x80 || format is >= 0x200 and <= 0x3ff)
                { snapshot.Dispose(); return null; }
                var size = GlobalSize(handle).ToUInt64();
                if (size == 0 || size > MaximumBytes || (total += (long)size) > MaximumBytes)
                { snapshot.Dispose(); return null; }
                var pointer = WindowsNativeMethods.GlobalLock(handle);
                if (pointer == IntPtr.Zero) { snapshot.Dispose(); return null; }
                try
                {
                    var bytes = new byte[(int)size];
                    Marshal.Copy(pointer, bytes, 0, bytes.Length);
                    snapshot._formats.Add((format, bytes, IntPtr.Zero));
                }
                finally { WindowsNativeMethods.GlobalUnlock(handle); }
            }
            return snapshot;
        }
        finally { WindowsNativeMethods.CloseClipboard(); }
    }

    internal bool TryRestore(uint expectedSequence, IntPtr expectedOwner)
    {
        var window = CreateWindowEx(0, "STATIC", "Yita.Clipboard", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero) return false;
        try
        {
            if (!WindowsNativeMethods.OpenClipboard(window)) return false;
            var handles = new List<(uint Format, IntPtr Handle, bool Bitmap)>();
            try
            {
                if (WindowsNativeMethods.GetClipboardSequenceNumber() != expectedSequence
                    || WindowsNativeMethods.GetClipboardOwner() != expectedOwner) return false;
                // Allocate the complete replacement before clearing anything.
                foreach (var entry in _formats)
                {
                    var handle = entry.Bitmap != IntPtr.Zero ? CopyImage(entry.Bitmap, 0, 0, 0, 0x2000)
                        : WindowsNativeMethods.GlobalAlloc(WindowsNativeMethods.GmemMoveable, (UIntPtr)entry.Bytes!.Length);
                    if (handle == IntPtr.Zero) return false;
                    handles.Add((entry.Format, handle, entry.Bitmap != IntPtr.Zero));
                    if (entry.Bytes is { } bytes)
                    {
                        var pointer = WindowsNativeMethods.GlobalLock(handle);
                        if (pointer == IntPtr.Zero) return false;
                        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
                        finally { WindowsNativeMethods.GlobalUnlock(handle); }
                    }
                }
                if (!WindowsNativeMethods.EmptyClipboard()) return false;
                for (var index = 0; index < handles.Count; index++)
                {
                    var entry = handles[index];
                    if (WindowsNativeMethods.SetClipboardData(entry.Format, entry.Handle) == IntPtr.Zero) return false;
                    handles[index] = (entry.Format, IntPtr.Zero, entry.Bitmap);
                }
                return true;
            }
            finally
            {
                foreach (var entry in handles)
                    if (entry.Handle != IntPtr.Zero)
                    {
                        if (entry.Bitmap) DeleteObject(entry.Handle);
                        else WindowsNativeMethods.GlobalFree(entry.Handle);
                    }
                WindowsNativeMethods.CloseClipboard();
            }
        }
        finally { DestroyWindow(window); }
    }

    public void Dispose()
    {
        foreach (var entry in _formats)
        {
            if (entry.Bitmap != IntPtr.Zero) DeleteObject(entry.Bitmap);
            if (entry.Bytes is { } bytes) Array.Clear(bytes);
        }
        _formats.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("user32.dll")] private static extern IntPtr CopyImage(IntPtr image, uint type, int width, int height, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
}
