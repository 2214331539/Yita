using System.Runtime.InteropServices;
using System.Text;
using Yita.Core.Selection;

namespace Yita.Native.Windows;

/// <summary>
/// Reads classic Edit/RichEdit and Scintilla selections without touching the
/// clipboard. This covers many editors and text fields while UI Automation is
/// being migrated into the Windows adapter.
/// </summary>
public sealed class WindowsNativeControlSelectionReader : ISelectionReader
{
    private const int MaximumTextLength = 20_000;
    private const int MaximumAncestorDepth = 8;
    private const uint EmGetSel = 0x00B0;
    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int GwlStyle = -16;
    private const long EsPassword = 0x20;
    private const int MessageTimeoutMilliseconds = 60;

    public Task<SelectionResult> ReadAsync(
        SelectionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(SelectionResult.Failed(SelectionFailureKind.UnsupportedApplication, "windows-only"));
        return Task.Run(() => ReadSelection(request, cancellationToken), cancellationToken);
    }

    private static SelectionResult ReadSelection(SelectionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hit = WindowsNativeMethods.WindowFromPoint(new WindowsNativeMethods.Point
        {
            X = (int)Math.Round(request.Pointer.X),
            Y = (int)Math.Round(request.Pointer.Y),
        });
        if (hit == IntPtr.Zero)
            return SelectionResult.Failed(SelectionFailureKind.Empty, "no-window");

        var current = hit;
        for (var depth = 0; current != IntPtr.Zero && depth < MaximumAncestorDepth; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var className = GetClassName(current);
            if (IsPassword(current))
                return SelectionResult.Failed(SelectionFailureKind.ProtectedContent, "password-control");
            if (IsSupportedClass(className) && TryReadSelection(current, out var text))
            {
                return new SelectionResult(
                    text,
                    SelectionSource.Accessibility,
                    new SelectionBounds(request.Pointer.X, request.Pointer.Y, 0, 0),
                    SelectionFailureKind.None,
                    "native-control");
            }
            current = WindowsNativeMethods.GetParent(current);
        }

        return SelectionResult.Failed(SelectionFailureKind.Empty, "native-control-empty");
    }

    private static bool IsSupportedClass(string? className) =>
        className is not null &&
        (className.Equals("Edit", StringComparison.OrdinalIgnoreCase)
         || className.Equals("RichEdit20A", StringComparison.OrdinalIgnoreCase)
         || className.Equals("RichEdit20W", StringComparison.OrdinalIgnoreCase)
         || className.Equals("RICHEDIT50W", StringComparison.OrdinalIgnoreCase)
         || className.StartsWith("WindowsForms10.EDIT", StringComparison.OrdinalIgnoreCase)
         || className.Equals("Scintilla", StringComparison.OrdinalIgnoreCase));

    private static bool IsPassword(IntPtr window) =>
        (WindowsNativeMethods.GetWindowLongPtr(window, GwlStyle).ToInt64() & EsPassword) != 0;

    private static bool TryReadSelection(IntPtr window, out string text)
    {
        text = string.Empty;
        var startMemory = Marshal.AllocHGlobal(sizeof(int));
        var endMemory = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (WindowsNativeMethods.SendMessageTimeout(
                    window, EmGetSel, ToUIntPtr(startMemory), endMemory,
                    SmtoAbortIfHung, MessageTimeoutMilliseconds, out _) == IntPtr.Zero)
                return false;
            var start = Marshal.ReadInt32(startMemory);
            var end = Marshal.ReadInt32(endMemory);
            if (start < 0 || end <= start) return false;
            var length = Math.Min(ReadTextLength(window), start + MaximumTextLength);
            if (length <= start) return false;

            var buffer = Marshal.AllocHGlobal((length + 1) * sizeof(char));
            try
            {
                if (WindowsNativeMethods.SendMessageTimeout(
                        window, WmGetText, new UIntPtr((uint)(length + 1)), buffer,
                        SmtoAbortIfHung, MessageTimeoutMilliseconds, out _) == IntPtr.Zero)
                    return false;
                var wholeText = Marshal.PtrToStringUni(buffer);
                if (string.IsNullOrEmpty(wholeText)) return false;
                var safeStart = Math.Clamp(start, 0, wholeText.Length);
                var safeEnd = Math.Clamp(end, safeStart, wholeText.Length);
                text = wholeText[safeStart..safeEnd].Trim();
                return text.Length > 0;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally
        {
            Marshal.FreeHGlobal(startMemory);
            Marshal.FreeHGlobal(endMemory);
        }
    }

    private static int ReadTextLength(IntPtr window)
    {
        if (WindowsNativeMethods.SendMessageTimeout(
                window, WmGetTextLength, UIntPtr.Zero, IntPtr.Zero,
                SmtoAbortIfHung, MessageTimeoutMilliseconds, out var result) == IntPtr.Zero)
            return 0;
        return Math.Min((int)Math.Min(result.ToUInt64(), 2_000_000), MaximumTextLength + 1);
    }

    private static string GetClassName(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return WindowsNativeMethods.GetClassName(window, buffer, buffer.Capacity) == 0
            ? string.Empty
            : buffer.ToString();
    }

    private static UIntPtr ToUIntPtr(IntPtr pointer) =>
        new(unchecked((ulong)pointer.ToInt64()));
}
