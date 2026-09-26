using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Yita.Models;
using Yita.Interop;
using Yita.Services;

namespace Yita.Selection;

internal sealed class UiaSelectionReader : IContextualSelectionReader
{
    private const int MaximumTextLength = 20_000;
    private const int MaximumContextLength = 3000;
    // Chromium's PDF accessibility tree can put the TextPattern document more
    // than ten control-view nodes above the glyph under the pointer.
    internal const int MaximumAncestorDepth = 32;
    private const int MaximumConcurrentReads = 2;
    private readonly SemaphoreSlim _readSlots = new(MaximumConcurrentReads, MaximumConcurrentReads);
    private readonly ConcurrentDictionary<uint, byte> _activeTargetProcesses = new();
    private readonly Func<ScreenPoint, bool, CancellationToken, SelectionCapture?> _readSelection;
    private readonly Func<ScreenPoint, uint?> _getTargetProcessId;
    private int _activeReadCount;
    private long _rejectedReadCount;
    private long _emptyReadCount;
    private long _successfulReadCount;

    public UiaSelectionReader()
        : this(ReadSelection, WindowProcessResolver.TryGetExternalProcessIdAt)
    {
    }

    internal UiaSelectionReader(
        Func<ScreenPoint, bool, CancellationToken, SelectionCapture?> readSelection,
        Func<ScreenPoint, uint?>? getTargetProcessId = null)
    {
        _readSelection = readSelection ?? throw new ArgumentNullException(nameof(readSelection));
        _getTargetProcessId = getTargetProcessId ?? (_ => null);
    }

    internal int ActiveReadCount => Volatile.Read(ref _activeReadCount);

    internal long RejectedReadCount => Interlocked.Read(ref _rejectedReadCount);

    public string CreateStatusReport(bool useChinese)
    {
        return useChinese
            ? string.Join(
                Environment.NewLine,
                "UI Automation 取词状态",
                $"正在读取：{ActiveReadCount}/{MaximumConcurrentReads}",
                $"读取成功：{Interlocked.Read(ref _successfulReadCount)} / 未公开选区：{Interlocked.Read(ref _emptyReadCount)}",
                $"Edge 可访问性激活/恢复：{EdgeAccessibilityActivator.ActivationCount}",
                $"为避免卡死已跳过：{RejectedReadCount}")
            : string.Join(
                Environment.NewLine,
                "UI Automation selection status",
                $"Active reads: {ActiveReadCount}/{MaximumConcurrentReads}",
                $"Selection found: {Interlocked.Read(ref _successfulReadCount)} / no selection exposed: {Interlocked.Read(ref _emptyReadCount)}",
                $"Edge accessibility activations/recoveries: {EdgeAccessibilityActivator.ActivationCount}",
                $"Skipped to avoid blocking: {RejectedReadCount}");
    }

    public async Task<string?> TryReadSelectedTextAsync(ScreenPoint point, CancellationToken cancellationToken)
    {
        var capture = await TryReadSelectionAsync(point, includeContext: false, cancellationToken)
            .ConfigureAwait(false);
        return capture?.Text;
    }

    public Task<SelectionCapture?> TryReadSelectionAsync(
        ScreenPoint point,
        bool includeContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var targetProcessId = _getTargetProcessId(point);
        if (targetProcessId is { } processId
            && !_activeTargetProcesses.TryAdd(processId, 0))
        {
            Interlocked.Increment(ref _rejectedReadCount);
            return Task.FromResult<SelectionCapture?>(null);
        }

        // UI Automation providers run in other processes. A broken provider can
        // remain blocked even after the caller's timeout expires, because COM
        // calls cannot be cancelled safely from managed code. Bound the number
        // of physical reads so repeated selections in such an application never
        // consume an unbounded number of ThreadPool threads. One remaining slot
        // still allows another healthy application to recover automatically.
        if (!_readSlots.Wait(0))
        {
            if (targetProcessId is { } rejectedProcessId)
            {
                _activeTargetProcesses.TryRemove(rejectedProcessId, out _);
            }

            Interlocked.Increment(ref _rejectedReadCount);
            return Task.FromResult<SelectionCapture?>(null);
        }

        Interlocked.Increment(ref _activeReadCount);
        var completion = new TaskCompletionSource<SelectionCapture?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var readThread = new Thread(
                () => ExecutePhysicalRead(
                    point,
                    includeContext,
                    targetProcessId,
                    cancellationToken,
                    completion))
            {
                IsBackground = true,
                Name = "Yita.UIAutomationRead",
            };
            readThread.Start();
        }
        catch
        {
            if (targetProcessId is { } failedProcessId)
            {
                _activeTargetProcesses.TryRemove(failedProcessId, out _);
            }

            Interlocked.Decrement(ref _activeReadCount);
            _readSlots.Release();
            throw;
        }

        var readTask = completion.Task;

        // WaitAsync cancellation deliberately does not release the physical-read
        // slot. The slot belongs to the underlying COM operation and is released
        // only when that operation actually ends. The worker also requests COM
        // call cancellation as a best effort so a responsive provider can unwind.
        ObserveFault(readTask);
        return readTask.WaitAsync(cancellationToken);
    }

    private void ExecutePhysicalRead(
        ScreenPoint point,
        bool includeContext,
        uint? targetProcessId,
        CancellationToken cancellationToken,
        TaskCompletionSource<SelectionCapture?> completion)
    {
        var coInitializeResult = NativeMethods.CoInitializeEx(
            IntPtr.Zero,
            NativeMethods.CoInitMultithreaded);
        var shouldUninitializeCom = coInitializeResult >= 0;
        var cancellationEnabled = coInitializeResult >= 0
                                  && NativeMethods.CoEnableCallCancellation(IntPtr.Zero) >= 0;
        CancellationTokenRegistration cancellationRegistration = default;
        SelectionCapture? result = null;
        Exception? failure = null;
        try
        {
            if (cancellationEnabled)
            {
                var threadId = NativeMethods.GetCurrentThreadId();
                cancellationRegistration = cancellationToken.UnsafeRegister(
                    static state =>
                    {
                        var targetThreadId = (uint)state!;
                        _ = NativeMethods.CoCancelCall(targetThreadId, 0);
                    },
                    threadId);
            }

            result = _readSelection(point, includeContext, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result?.Text))
            {
                Interlocked.Increment(ref _emptyReadCount);
            }
            else
            {
                Interlocked.Increment(ref _successfulReadCount);
            }
        }
        catch (OperationCanceledException exception)
        {
            failure = exception;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            cancellationRegistration.Dispose();
            if (cancellationEnabled)
            {
                _ = NativeMethods.CoDisableCallCancellation(IntPtr.Zero);
            }

            if (shouldUninitializeCom)
            {
                NativeMethods.CoUninitialize();
            }

            if (targetProcessId is { } processId)
            {
                _activeTargetProcesses.TryRemove(processId, out _);
            }

            Interlocked.Decrement(ref _activeReadCount);
            _readSlots.Release();
        }

        if (failure is OperationCanceledException canceled)
        {
            completion.TrySetCanceled(canceled.CancellationToken);
        }
        else if (failure is not null)
        {
            completion.TrySetException(failure);
        }
        else
        {
            completion.TrySetResult(result);
        }
    }

    private static void ObserveFault(Task<SelectionCapture?> task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static SelectionCapture? ReadSelection(
        ScreenPoint point,
        bool includeContext,
        CancellationToken cancellationToken)
    {
        EdgeAccessibilityActivator.ActivateIfNeeded(point, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var capture = ReadCurrentSelection(point, includeContext, cancellationToken);
        if (capture is null
            && EdgeAccessibilityActivator.ActivateIfNeeded(point, cancellationToken, refresh: true))
        {
            // Never reuse a hit, range, pattern, or page node after activation:
            // a scrolled/zoomed PDF may now have an entirely new renderer tree.
            capture = ReadCurrentSelection(point, includeContext, cancellationToken);
        }

        return capture;
    }

    private static SelectionCapture? ReadCurrentSelection(
        ScreenPoint point,
        bool includeContext,
        CancellationToken cancellationToken)
    {
        var expectedRootOwner = GetRootOwnerAt(point);
        if (expectedRootOwner == IntPtr.Zero)
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(expectedRootOwner, out var ownerProcessId);
        if (ownerProcessId == 0 || ownerProcessId == (uint)Environment.ProcessId)
        {
            return null;
        }

        var isDocumentHost = IsDocumentHostProcess((int)ownerProcessId);
        AutomationElement? hitElement = null;
        try
        {
            hitElement = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
        }
        catch (Exception exception) when (SelectionCandidateSearch.IsUnavailable(exception))
        {
        }

        AutomationElement? GetFocusedCandidate()
        {
            try
            {
                var candidate = AutomationElement.FocusedElement;
                if (ShouldIncludeFocusedElementInTargetWindow(
                        TryGetProcessId(hitElement) ?? (int)ownerProcessId,
                        TryGetProcessId(candidate),
                        expectedRootOwner,
                        TryGetRootOwnerHandle(candidate),
                        allowEmbeddedProcess: isDocumentHost))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (SelectionCandidateSearch.IsUnavailable(exception))
            {
            }

            return null;
        }

        return SelectionCandidateSearch.Read(
            hitElement,
            GetFocusedCandidate,
            element => element.Current.IsPassword ? SelectionNodeAccess.Protected : SelectionNodeAccess.Readable,
            element => new IntPtr(element.Current.NativeWindowHandle) == expectedRootOwner
                ? null
                : TreeWalker.RawViewWalker.GetParent(element),
            element => TryReadFromElement(element, includeContext),
            () => isDocumentHost
                ? FindDocumentCandidates(expectedRootOwner, point, cancellationToken)
                : Array.Empty<AutomationElement>(),
            cancellationToken);
    }

    private static IEnumerable<AutomationElement> FindDocumentCandidates(
        IntPtr expectedRootOwner,
        ScreenPoint point,
        CancellationToken cancellationToken)
    {
        AutomationElement windowRoot;
        try
        {
            windowRoot = AutomationElement.FromHandle(expectedRootOwner);
        }
        catch (Exception exception) when (SelectionCandidateSearch.IsUnavailable(exception))
        {
            yield break;
        }

        // Filter at the provider so hundreds of offscreen PDF pages cannot use
        // up the traversal budget before we reach the current viewport. Cache
        // only geometry/type/privacy properties, never names or document text.
        var visibleWalker = new TreeWalker(new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsPasswordProperty);

        IEnumerable<AutomationElement> Children(AutomationElement parent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var child = visibleWalker.GetFirstChild(parent, cache);
            while (child is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return child;
                child = visibleWalker.GetNextSibling(child, cache);
            }
        }

        DocumentNodeInfo Info(AutomationElement element)
        {
            var state = ReferenceEquals(element, windowRoot) ? element.Current : element.Cached;
            return new DocumentNodeInfo(
                state.ControlType == ControlType.Document,
                state.IsOffscreen,
                state.BoundingRectangle.Contains(point.X, point.Y),
                state.IsPassword);
        }

        foreach (var document in VisibleDocumentSearch.Find(windowRoot, element => Info(element), Children, cancellationToken))
        {
            yield return document;
        }
    }

    private static bool IsDocumentHostProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase)
                   || process.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase)
                   || process.ProcessName.Equals("chromium", StringComparison.OrdinalIgnoreCase)
                   || process.ProcessName.Equals("brave", StringComparison.OrdinalIgnoreCase)
                   || process.ProcessName.Equals("firefox", StringComparison.OrdinalIgnoreCase)
                   || process.ProcessName.Equals("zotero", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal static bool ShouldIncludeFocusedElement(int? hitProcessId, int? focusedProcessId)
    {
        return hitProcessId is > 0 && hitProcessId == focusedProcessId;
    }

    internal static bool ShouldIncludeFocusedElementInTargetWindow(
        int? hitProcessId,
        int? focusedProcessId,
        IntPtr expectedRootOwner,
        IntPtr focusedRootOwner,
        bool allowEmbeddedProcess = false)
    {
        // A PDF renderer may use a different PID from the browser's HWND after
        // navigation or relayout. Accept that only with proven window ownership.
        if (allowEmbeddedProcess && focusedProcessId is > 0
            && expectedRootOwner != IntPtr.Zero && expectedRootOwner == focusedRootOwner)
        {
            return true;
        }

        if (!ShouldIncludeFocusedElement(hitProcessId, focusedProcessId))
        {
            return false;
        }

        // Chromium/Electron document hosts commonly omit NativeWindowHandle
        // on the focused accessibility node. A same-process focus is still
        // the active document the user just selected in; reject only when a
        // concrete root owner proves it belongs to another top-level window.
        return expectedRootOwner == IntPtr.Zero
               || focusedRootOwner == IntPtr.Zero
               || focusedRootOwner == expectedRootOwner;
    }

    private static IntPtr GetRootOwnerAt(ScreenPoint point)
    {
        var windowHandle = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint
        {
            X = point.X,
            Y = point.Y,
        });
        return windowHandle == IntPtr.Zero
            ? IntPtr.Zero
            : GetRootOwner(windowHandle);
    }

    private static IntPtr TryGetRootOwnerHandle(AutomationElement? element)
    {
        var current = element;
        for (var depth = 0; current is not null && depth < MaximumAncestorDepth; depth++)
        {
            try
            {
                var windowHandle = new IntPtr(current.Current.NativeWindowHandle);
                if (windowHandle != IntPtr.Zero)
                {
                    return GetRootOwner(windowHandle);
                }

                current = TreeWalker.RawViewWalker.GetParent(current);
            }
            catch (ElementNotAvailableException)
            {
                return IntPtr.Zero;
            }
            catch (InvalidOperationException)
            {
                return IntPtr.Zero;
            }
            catch (COMException)
            {
                return IntPtr.Zero;
            }
        }

        return IntPtr.Zero;
    }

    private static IntPtr GetRootOwner(IntPtr windowHandle)
    {
        var rootOwner = NativeMethods.GetAncestor(windowHandle, NativeMethods.GaRootOwner);
        return rootOwner != IntPtr.Zero
            ? rootOwner
            : NativeMethods.GetAncestor(windowHandle, NativeMethods.GaRoot);
    }

    private static int? TryGetProcessId(AutomationElement? element)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            return element.Current.ProcessId;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static SelectionCapture? TryReadFromElement(AutomationElement element, bool includeContext)
    {
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject)
                || patternObject is not TextPattern textPattern)
            {
                return null;
            }

            var ranges = textPattern.GetSelection();
            if (ranges is null || ranges.Length == 0)
            {
                return null;
            }

            var remaining = MaximumTextLength;
            var selectedParts = new List<string>(ranges.Length);
            var contexts = includeContext
                ? new BoundedTextAccumulator(MaximumContextLength)
                : null;
            foreach (var range in ranges)
            {
                if (remaining <= 0)
                {
                    break;
                }

                var value = TextNormalizer.Normalize(range.GetText(remaining));
                if (value.Length == 0)
                {
                    continue;
                }

                selectedParts.Add(value);
                remaining -= value.Length;

                if (contexts is not null
                    && contexts.MaximumNextPartLength > 0
                    && TryReadParagraphContext(range, contexts.MaximumNextPartLength) is { Length: > 0 } context
                    && !string.Equals(context, value, StringComparison.Ordinal))
                {
                    contexts.TryAdd(context);
                }
            }

            return selectedParts.Count == 0
                ? null
                : new SelectionCapture(
                    string.Join(Environment.NewLine, selectedParts),
                    contexts?.Build());
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string? TryReadParagraphContext(
        TextPatternRange selectionRange,
        int maximumLength)
    {
        try
        {
            var paragraphRange = selectionRange.Clone();
            paragraphRange.ExpandToEnclosingUnit(TextUnit.Paragraph);
            var context = TextNormalizer.Normalize(paragraphRange.GetText(maximumLength));
            return context.Length == 0 ? null : context;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

}
