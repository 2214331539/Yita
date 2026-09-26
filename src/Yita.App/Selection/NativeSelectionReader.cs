using System.Runtime.InteropServices;
using System.Text;
using Yita.Interop;
using Yita.Models;

namespace Yita.Selection;

/// <summary>
/// Reads selections directly from classic Win32/RichEdit and Scintilla
/// controls. These controls expose their selected range through messages, so
/// this reader never sends WM_COPY and never changes the system clipboard.
/// </summary>
internal sealed class NativeSelectionReader : ISelectionReader
{
    private const int MaximumTextLength = 20000;
    private const int MaximumDocumentPrefixLength = 2_000_000;
    private const uint MessageTimeoutMilliseconds = 60;
    private const int MaximumAncestorDepth = 8;

    public Task<string?> TryReadSelectedTextAsync(
        ScreenPoint point,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => ReadSelection(point, cancellationToken), cancellationToken);
    }

    private static string? ReadSelection(ScreenPoint point, CancellationToken cancellationToken)
    {
        var hitWindow = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint
        {
            X = point.X,
            Y = point.Y,
        });
        if (!IsExternalWindow(hitWindow))
        {
            return null;
        }

        var current = hitWindow;
        for (var depth = 0; current != IntPtr.Zero && depth < MaximumAncestorDepth; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var className = GetClassName(current);
            if (IsEditControl(className))
            {
                if (IsPasswordStyle(NativeMethods.GetWindowLongPtr(current, NativeMethods.GwlStyle).ToInt64()))
                {
                    return null;
                }

                var selectedText = TryReadEditSelection(current);
                if (!string.IsNullOrWhiteSpace(selectedText))
                {
                    return selectedText;
                }
            }
            else if (IsScintillaControl(className))
            {
                // Avoid SCI_GETSELTEXT: custom messages above WM_USER do not
                // marshal pointers cross-process. Scintilla still implements the
                // system EM_GETSEL/WM_GETTEXT compatibility path, which Windows
                // can marshal safely with the same bounded reader used for Edit.
                var selectedText = TryReadEditRangeSelection(current);
                return string.IsNullOrWhiteSpace(selectedText) ? null : selectedText;
            }

            current = NativeMethods.GetParent(current);
        }

        return null;
    }

    internal static bool IsEditControl(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        return className.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || className.Equals("RichEdit20A", StringComparison.OrdinalIgnoreCase)
            || className.Equals("RichEdit20W", StringComparison.OrdinalIgnoreCase)
            || className.Equals("RICHEDIT50W", StringComparison.OrdinalIgnoreCase)
            || className.StartsWith("WindowsForms10.EDIT", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsScintillaControl(string? className)
    {
        return className?.Equals("Scintilla", StringComparison.OrdinalIgnoreCase) == true;
    }

    internal static bool IsPasswordStyle(long windowStyle)
    {
        return (windowStyle & NativeMethods.EsPassword) != 0;
    }

    private static string? TryReadEditSelection(IntPtr windowHandle)
    {
        // EM_GETSELTEXT has no buffer-length parameter. Sending it directly to
        // another process with a fixed local buffer lets a long selection write
        // past that allocation. EM_GETSEL followed by the bounded WM_GETTEXT
        // system message is slower but is safely marshalled and length-limited.
        return TryReadEditRangeSelection(windowHandle);
    }

    private static string? TryReadEditRangeSelection(IntPtr windowHandle)
    {
        var startPointer = Marshal.AllocHGlobal(sizeof(int));
        var endPointer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            var sent = NativeMethods.SendMessageTimeout(
                windowHandle,
                NativeMethods.EmGetSel,
                ToUIntPtr(startPointer),
                endPointer,
                NativeMethods.SmtoAbortIfHung,
                MessageTimeoutMilliseconds,
                out _);
            if (sent == IntPtr.Zero)
            {
                return null;
            }

            var start = Marshal.ReadInt32(startPointer);
            var end = Marshal.ReadInt32(endPointer);
            var textLength = TryGetWindowTextLength(windowHandle);
            if (!TryCalculateBoundedRead(
                    start,
                    end,
                    textLength,
                    out var requestedEnd,
                    out var bufferLength))
            {
                return null;
            }

            var textBuffer = Marshal.AllocHGlobal(bufferLength * sizeof(char));
            try
            {
                var textBufferSize = bufferLength * sizeof(char);
                Marshal.Copy(new byte[textBufferSize], 0, textBuffer, textBufferSize);
                var textSent = NativeMethods.SendMessageTimeout(
                    windowHandle,
                    NativeMethods.WmGetText,
                    new UIntPtr((uint)bufferLength),
                    textBuffer,
                    NativeMethods.SmtoAbortIfHung,
                    MessageTimeoutMilliseconds,
                    out _);
                if (textSent == IntPtr.Zero)
                {
                    return null;
                }

                var wholeText = Marshal.PtrToStringUni(textBuffer);
                if (string.IsNullOrEmpty(wholeText))
                {
                    return null;
                }

                var safeStart = Math.Clamp(start, 0, wholeText.Length);
                var safeEnd = Math.Clamp(requestedEnd, safeStart, wholeText.Length);
                return TextNormalizer.Normalize(wholeText[safeStart..safeEnd]);
            }
            finally
            {
                Marshal.FreeHGlobal(textBuffer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(startPointer);
            Marshal.FreeHGlobal(endPointer);
        }
    }

    private static int TryGetWindowTextLength(IntPtr windowHandle)
    {
        var sent = NativeMethods.SendMessageTimeout(
            windowHandle,
            NativeMethods.WmGetTextLength,
            UIntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.SmtoAbortIfHung,
            MessageTimeoutMilliseconds,
            out var result);
        if (sent == IntPtr.Zero)
        {
            return 0;
        }

        return (int)Math.Min(result.ToUInt64(), MaximumDocumentPrefixLength);
    }

    internal static bool TryCalculateBoundedRead(
        int selectionStart,
        int selectionEnd,
        int textLength,
        out int requestedEnd,
        out int bufferLength)
    {
        requestedEnd = 0;
        bufferLength = 0;
        if (selectionStart < 0 || selectionEnd <= selectionStart || textLength <= selectionStart)
        {
            return false;
        }

        requestedEnd = (int)Math.Min(
            (long)selectionEnd,
            (long)selectionStart + MaximumTextLength);
        var lastCharacterToRead = Math.Min(
            textLength,
            Math.Min(requestedEnd, MaximumDocumentPrefixLength));
        if (lastCharacterToRead <= selectionStart)
        {
            requestedEnd = 0;
            return false;
        }

        bufferLength = lastCharacterToRead + 1;
        return true;
    }

    private static UIntPtr ToUIntPtr(IntPtr pointer)
    {
        return new UIntPtr(unchecked((ulong)pointer.ToInt64()));
    }

    private static string GetClassName(IntPtr windowHandle)
    {
        var buffer = new StringBuilder(256);
        var length = NativeMethods.GetClassName(windowHandle, buffer, buffer.Length);
        return length <= 0 ? string.Empty : buffer.ToString();
    }

    private static bool IsExternalWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        return processId != 0 && processId != (uint)Environment.ProcessId;
    }
}
