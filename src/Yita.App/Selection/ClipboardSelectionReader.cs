using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Yita.Interop;
using Yita.Models;
using WpfClipboard = System.Windows.Clipboard;
using WpfDataObject = System.Windows.IDataObject;
using WpfTextDataFormat = System.Windows.TextDataFormat;

namespace Yita.Selection;

internal sealed class ClipboardSelectionReader : ISelectionReader
{
    private static readonly SemaphoreSlim ClipboardTransactionGate = new(1, 1);
    private static readonly TimeSpan ClipboardCopyTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PerTargetCopyTimeout = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ClipboardPollInterval = TimeSpan.FromMilliseconds(20);

    private readonly Dispatcher _dispatcher;
    private readonly Func<ScreenPoint, bool> _isFallbackAllowed;

    public ClipboardSelectionReader(
        Dispatcher dispatcher,
        Func<ScreenPoint, bool> isFallbackAllowed)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _isFallbackAllowed = isFallbackAllowed ?? throw new ArgumentNullException(nameof(isFallbackAllowed));
    }

    public async Task<string?> TryReadSelectedTextAsync(
        ScreenPoint point,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isFallbackAllowed(point))
        {
            return null;
        }

        var operation = _dispatcher.InvokeAsync(
            () => ReadClipboardSelectionAsync(point, cancellationToken),
            DispatcherPriority.Input);
        return await operation.Task.Unwrap().ConfigureAwait(false);
    }

    private static async Task<string?> ReadClipboardSelectionAsync(
        ScreenPoint point,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await ClipboardTransactionGate.WaitAsync(cancellationToken);
        try
        {
            return await ReadClipboardSelectionTransactionAsync(point, cancellationToken);
        }
        finally
        {
            ClipboardTransactionGate.Release();
        }
    }

    private static async Task<string?> ReadClipboardSelectionTransactionAsync(
        ScreenPoint point,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        WpfDataObject? previousClipboard = null;
        var originalSequence = NativeMethods.GetClipboardSequenceNumber();
        try
        {
            previousClipboard = WpfClipboard.GetDataObject();
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        // The snapshot and the copy attempt must describe one clipboard state.
        // If another application changed it while the snapshot was being read,
        // do not risk restoring stale data over that newer content.
        if (NativeMethods.GetClipboardSequenceNumber() != originalSequence)
        {
            return null;
        }

        uint? copiedClipboardSequence = null;
        try
        {
            var copyTargets = ResolveCopyTargets(point);
            if (copyTargets.Count == 0)
            {
                return null;
            }

            var deadline = DateTime.UtcNow + ClipboardCopyTimeout;
            for (var targetIndex = 0; targetIndex < copyTargets.Count; targetIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sequenceBeforeCopy = NativeMethods.GetClipboardSequenceNumber();
                if (sequenceBeforeCopy != originalSequence)
                {
                    return null;
                }

                if (!SendCopyMessage(copyTargets[targetIndex]))
                {
                    continue;
                }

                var isLastTarget = targetIndex == copyTargets.Count - 1;
                var targetDeadline = isLastTarget
                    ? deadline
                    : Min(deadline, DateTime.UtcNow + PerTargetCopyTimeout);
                // Once WM_COPY has been delivered, finish the short clipboard
                // transaction even if the translation request was superseded.
                // Otherwise cancellation could leave our copied text behind.
                var copyResult = await WaitForCopiedTextAsync(
                    sequenceBeforeCopy,
                    targetDeadline,
                    CancellationToken.None);

                if (copyResult.Sequence is not null)
                {
                    copiedClipboardSequence = copyResult.Sequence;
                    return copyResult.Text;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    break;
                }
            }

            return null;
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (previousClipboard is not null
                && copiedClipboardSequence is { } expectedSequence
                && NativeMethods.GetClipboardSequenceNumber() == expectedSequence)
            {
                try
                {
                    WpfClipboard.SetDataObject(previousClipboard, true);
                }
                catch (ExternalException)
                {
                    // Clipboard ownership can change while restoring. The selected
                    // text was already copied to a local string, so keep the request
                    // alive even if restoration is refused by another application.
                }
                catch (InvalidOperationException)
                {
                }
                catch (ArgumentException)
                {
                }
            }
        }
    }

    private static async Task<ClipboardCopyResult> WaitForCopiedTextAsync(
        uint sequenceBeforeCopy,
        DateTime deadline,
        CancellationToken cancellationToken)
    {
        uint? observedSequence = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentSequence = NativeMethods.GetClipboardSequenceNumber();

            if (observedSequence is null && currentSequence != sequenceBeforeCopy)
            {
                observedSequence = currentSequence;
            }
            else if (observedSequence is { } expectedSequence && currentSequence != expectedSequence)
            {
                // A second change cannot safely be attributed to our WM_COPY.
                return default;
            }

            if (observedSequence is { } copiedSequence
                && TryReadClipboardText(out var selectedText))
            {
                return new ClipboardCopyResult(selectedText, copiedSequence);
            }

            await Task.Delay(ClipboardPollInterval, cancellationToken);
        }

        return observedSequence is { } sequence
            ? new ClipboardCopyResult(null, sequence)
            : default;
    }

    private static bool TryReadClipboardText(out string? selectedText)
    {
        selectedText = null;
        try
        {
            if (!WpfClipboard.ContainsText(WpfTextDataFormat.UnicodeText))
            {
                return false;
            }

            var text = TextNormalizer.Normalize(WpfClipboard.GetText(WpfTextDataFormat.UnicodeText));
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            selectedText = text;
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IReadOnlyList<IntPtr> ResolveCopyTargets(ScreenPoint point)
    {
        var windowHandle = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint
        {
            X = point.X,
            Y = point.Y,
        });

        if (NativeSelectionReader.IsPasswordStyle(
                NativeMethods.GetWindowLongPtr(windowHandle, NativeMethods.GwlStyle).ToInt64())
            || !TryGetExternalProcessId(windowHandle, out var processId))
        {
            return Array.Empty<IntPtr>();
        }

        var targets = new List<IntPtr>(capacity: 2) { windowHandle };
        var rootTarget = NativeMethods.GetAncestor(windowHandle, NativeMethods.GaRoot);
        if (rootTarget != IntPtr.Zero
            && rootTarget != windowHandle
            && TryGetProcessId(rootTarget, out var rootProcessId)
            && rootProcessId == processId)
        {
            targets.Add(rootTarget);
        }

        return targets;
    }

    private static bool SendCopyMessage(IntPtr windowHandle)
    {
        return NativeMethods.SendMessageTimeout(
            windowHandle,
            NativeMethods.WmCopy,
            UIntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.SmtoAbortIfHung,
            100,
            out _) != IntPtr.Zero;
    }

    private static DateTime Min(DateTime first, DateTime second) => first <= second ? first : second;

    private static bool TryGetExternalProcessId(IntPtr windowHandle, out uint processId)
    {
        return TryGetProcessId(windowHandle, out processId)
            && processId != (uint)Environment.ProcessId;
    }

    private static bool TryGetProcessId(IntPtr windowHandle, out uint processId)
    {
        processId = 0;
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
        return processId != 0;
    }

    private readonly record struct ClipboardCopyResult(string? Text, uint? Sequence);
}
