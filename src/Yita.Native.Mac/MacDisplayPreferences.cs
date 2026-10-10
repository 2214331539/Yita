using System.Runtime.InteropServices;

namespace Yita.Native.Mac;

public static class MacDisplayPreferences
{
    public static bool ReduceMotion
    {
        get
        {
            if (!OperatingSystem.IsMacOS()) return false;
            _ = AppKit.Value;
            if (SendBoolean(GetClass("NSThread"), Selector("isMainThread")) == 0)
                throw new InvalidOperationException("macOS display preferences must be read on the Cocoa main thread.");
            var workspace = SendObject(GetClass("NSWorkspace"), Selector("sharedWorkspace"));
            if (workspace == 0) throw new InvalidOperationException("macOS workspace is unavailable.");
            return SendBoolean(workspace, Selector("accessibilityDisplayShouldReduceMotion")) != 0;
        }
    }

    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    private static readonly Lazy<nint> AppKit = new(() => NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit"));
    [DllImport(ObjectiveC, EntryPoint = "objc_getClass")]
    private static extern nint GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjectiveC, EntryPoint = "sel_registerName")]
    private static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint SendObject(nint receiver, nint selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern byte SendBoolean(nint receiver, nint selector);
}
