using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Yita.Interop;
using Yita.Models;
using Yita.Services;
using WpfClipboard = System.Windows.Clipboard;
using WpfDataObject = System.Windows.IDataObject;
using WpfTextDataFormat = System.Windows.TextDataFormat;

namespace Yita.Selection;

internal sealed class ClipboardSelectionReader : ISelectionReader
{
    private static readonly SemaphoreSlim ClipboardTransactionGate = new(1, 1);
    private static readonly TimeSpan ClipboardCopyTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PerTargetCopyTimeout = TimeSpan.FromMilliseconds(180);

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
            var isWpsPdf = WindowProcessResolver.IsWpsPdfAt(point);
            var started = System.Diagnostics.Stopwatch.StartNew();
            var text = await ReadClipboardSelectionTransactionAsync(point, cancellationToken, isWpsPdf);
            if (isWpsPdf)
                new RuntimeHealthJournal().Record(string.IsNullOrWhiteSpace(text)
                    ? RuntimeHealthEvent.WpsCopyEmpty : RuntimeHealthEvent.WpsCopySucceeded,
                    numericCode: (int)started.ElapsedMilliseconds);
            return text;
        }
        finally
        {
            ClipboardTransactionGate.Release();
        }
    }

    private static async Task<string?> ReadClipboardSelectionTransactionAsync(
        ScreenPoint point,
        CancellationToken cancellationToken,
        bool preferShortcut)
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
        IntPtr copiedClipboardOwner = IntPtr.Zero;
        try
        {
            var copyTargets = ResolveCopyTargets(point);
            if (copyTargets.Count == 0 || !CopyShortcut.IsTargetCurrent(copyTargets[0]))
            {
                return null;
            }

            // A WPS PDF canvas handles keyboard copy but often ignores WM_COPY.
            // Avoid spending the message timeout on every selection. Retry once
            // only if nothing was copied and the same target remains active.
            if (preferShortcut)
            {
                var result = await ReadShortcutSelectionAsync(copyTargets, originalSequence, cancellationToken, retry: true);
                copiedClipboardSequence = result.Sequence;
                copiedClipboardOwner = result.Owner;
                return result.Text;
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

                if (!CopyShortcut.IsTargetCurrent(copyTargets[targetIndex])) return null;
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
                    CancellationToken.None,
                    copyTargets[0]);

                if (copyResult.Sequence is not null)
                {
                    copiedClipboardSequence = copyResult.Sequence;
                    copiedClipboardOwner = copyResult.Owner;
                    return copyResult.Text;
                }
                if (copyResult.Changed) return null;

                if (DateTime.UtcNow >= deadline)
                {
                    break;
                }
            }

            // Custom PDF canvases often handle Ctrl+C, but ignore WM_COPY.
            // Keep this inside the same serialized clipboard transaction.
            var shortcutResult = await ReadShortcutSelectionAsync(copyTargets, originalSequence, cancellationToken, retry: false);
            copiedClipboardSequence = shortcutResult.Sequence;
            copiedClipboardOwner = shortcutResult.Owner;
            return shortcutResult.Text;
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
            if (copiedClipboardSequence is { } expectedSequence
                && NativeMethods.GetClipboardSequenceNumber() == expectedSequence
                && GetClipboardOwner() == copiedClipboardOwner)
            {
                try
                {
                    if (previousClipboard is null) WpfClipboard.Clear();
                    else WpfClipboard.SetDataObject(previousClipboard, true);
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

    private static async Task<ClipboardCopyResult> ReadShortcutSelectionAsync(
        IReadOnlyList<IntPtr> targets, uint sequence, CancellationToken cancellationToken, bool retry)
    {
        for (var attempt = 0; attempt < (retry ? 2 : 1); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.GetClipboardSequenceNumber() != sequence || !CopyShortcut.IsTargetCurrent(targets[0]))
                return default;
            if (CopyShortcut.TrySend(targets[0]))
            {
                // Once input was sent, finish its bounded transaction even if a
                // newer gesture cancels the request, then restore where safe.
                var budget = retry && attempt == 0 ? 250 : 650;
                var result = await WaitForCopiedTextAsync(sequence,
                    DateTime.UtcNow + TimeSpan.FromMilliseconds(budget), CancellationToken.None, targets[0]);
                if (result.Changed) return result;
            }
            if (retry && attempt == 0) await Task.Delay(80, cancellationToken);
        }
        return default;
    }

    private static Task<ClipboardCopyResult> WaitForCopiedTextAsync(
        uint sequenceBeforeCopy,
        DateTime deadline,
        CancellationToken cancellationToken,
        IntPtr target)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Capture only processes attached to the original window tree. Do not
        // accept arbitrary WPS processes, which may host a different document.
        var processIds = new HashSet<uint>();
        foreach (var window in new[] { target, NativeMethods.GetAncestor(target, NativeMethods.GaRootOwner), CopyShortcut.GetFocusedTarget(target) })
            if (TryGetExternalProcessId(window, out var processId)) processIds.Add(processId);
        return ClipboardCopyWaiter.WaitAsync(sequenceBeforeCopy, deadline - DateTime.UtcNow,
            () =>
            {
                var sequence = NativeMethods.GetClipboardSequenceNumber();
                var owner = GetClipboardOwner();
                NativeMethods.GetWindowThreadProcessId(owner, out var ownerProcess);
                return new ClipboardStamp(sequence, owner, processIds.Contains(ownerProcess));
            },
            () => TryReadClipboardText(out var text) ? text : null);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

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

}
