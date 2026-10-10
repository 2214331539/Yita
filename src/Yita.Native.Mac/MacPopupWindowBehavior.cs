using System.Runtime.InteropServices;

namespace Yita.Native.Mac;

/// <summary>Adjusts only the collection behavior of an existing Avalonia NSWindow.</summary>
public static class MacPopupWindowBehavior
{
    internal static nuint ResolveCollectionBehavior(nuint current) =>
        (current & ~((nuint)(1 << 1) | (nuint)(1 << 2) | (nuint)(1 << 5) | (nuint)(1 << 7) | (nuint)(1 << 9)))
        | (nuint)(1 << 0) | (nuint)(1 << 3) | (nuint)(1 << 6) | (nuint)(1 << 8);

    public static bool Apply(nint handle, string? descriptor)
    {
        if (!OperatingSystem.IsMacOS() || handle == 0 || descriptor != "NSWindow") return false;
        if (SendBoolean(GetClass("NSThread"), Selector("isMainThread")) == 0)
            throw new InvalidOperationException("macOS window behavior must be applied on the Cocoa main thread.");
        var current = SendUnsigned(handle, Selector("collectionBehavior"));
        SendUnsignedArgument(handle, Selector("setCollectionBehavior:"), ResolveCollectionBehavior(current));
        return true;
    }

    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjectiveC, EntryPoint = "objc_getClass")]
    private static extern nint GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjectiveC, EntryPoint = "sel_registerName")]
    private static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern byte SendBoolean(nint receiver, nint selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nuint SendUnsigned(nint receiver, nint selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern void SendUnsignedArgument(nint receiver, nint selector, nuint value);
}
