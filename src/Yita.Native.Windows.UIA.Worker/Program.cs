using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Yita.Core.Selection;

namespace Yita.Native.Windows.UIA.Worker;

internal static class Program
{
    private const string ReadyLine = "YITA-UIA-1";
    private const int MaximumTextLength = 20_000;
    private const int MaximumAncestorDepth = 32;
    private const int MaximumDocumentNodes = 256;
    private const int EdgeActivationPulseMilliseconds = 100;
    private const int EdgeActivationLifetimeMilliseconds = 15_000;
    private const int EdgeRefreshCooldownMilliseconds = 250;
    private const int SpiGetScreenReader = 0x0046;
    private const int SpiSetScreenReader = 0x0047;
    private const uint SpifSendChange = 0x0002;
    private const uint GaRoot = 2;
    private const uint GaRootOwner = 3;

    private static readonly object EdgeActivationSync = new();
    private static readonly Dictionary<(uint ProcessId, IntPtr Window), long> EdgeActivations = new();

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--package-check"])
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "passed", architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                runtime = RuntimeInformation.FrameworkDescription,
                version = typeof(Program).Assembly.GetName().Version?.ToString(),
                automation = typeof(AutomationElement).Assembly.GetName().Name
            }));
            return 0;
        }
        var parentId = ReadParentId(args);
        if (parentId is { } id)
        {
            try
            {
                var parent = Process.GetProcessById(id);
                _ = WaitForParentAsync(parent);
            }
            catch (ArgumentException)
            {
                return 2;
            }
        }

        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        await output.WriteLineAsync(ReadyLine).ConfigureAwait(false);
        while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.Length > 8_000) return 2;

            WorkerRequest? request;
            try { request = JsonSerializer.Deserialize<WorkerRequest>(line); }
            catch (JsonException) { return 2; }
            if (request is null) return 2;

            WorkerResponse response;
            try
            {
                response = ReadSelection(request);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                response = Empty("uia-provider-error");
            }

            await output.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
        }

        return 0;
    }

    private static WorkerResponse ReadSelection(WorkerRequest request)
    {
        if (!double.IsFinite(request.X) || !double.IsFinite(request.Y))
            return Empty("invalid-point");

        var comResult = NativeMethods.CoInitializeEx(IntPtr.Zero, NativeMethods.CoInitMultithreaded);
        var shouldUninitializeCom = comResult >= 0;
        try
        {
            var point = new Point(request.X, request.Y);
            ActivateEdgeAccessibility(point, refresh: false);

            var response = ReadCurrentSelection(point, request.IncludeContext);
            if (response is not null) return response;

            // Edge may rebuild its renderer tree after scrolling or zooming.
            // A second, window-scoped accessibility pulse gives it one bounded
            // recovery attempt without enabling screen-reader mode permanently.
            if (ActivateEdgeAccessibility(point, refresh: true))
            {
                response = ReadCurrentSelection(point, request.IncludeContext);
                if (response is not null) return response;
            }

            return Empty("uia-selection-empty");
        }
        finally
        {
            if (shouldUninitializeCom) NativeMethods.CoUninitialize();
        }
    }

    private static WorkerResponse? ReadCurrentSelection(Point point, bool includeContext)
    {
        var expectedRootOwner = GetRootOwnerAt(point);
        if (expectedRootOwner == IntPtr.Zero) return null;

        NativeMethods.GetWindowThreadProcessId(expectedRootOwner, out var ownerProcessId);
        if (ownerProcessId == 0 || ownerProcessId == (uint)Environment.ProcessId) return null;

        var documentHost = IsDocumentHostProcess(ownerProcessId, expectedRootOwner);
        AutomationElement? hitElement = null;
        TryGet(() => AutomationElement.FromPoint(point), value => hitElement = value);

        var candidates = new List<AutomationElement>();
        if (hitElement is not null) candidates.Add(hitElement);

        AutomationElement? focused = null;
        TryGet(() => AutomationElement.FocusedElement, value => focused = value);
        if (focused is not null && ShouldIncludeFocusedElement(
                focused,
                ownerProcessId,
                expectedRootOwner,
                allowEmbeddedProcess: documentHost))
        {
            if (!candidates.Contains(focused)) candidates.Add(focused);
        }

        foreach (var candidate in candidates)
        {
            var result = ReadCandidatePath(candidate, includeContext);
            if (result is not null) return result;
        }

        if (!documentHost) return null;

        foreach (var document in FindVisibleDocumentCandidates(expectedRootOwner, point))
        {
            var result = ReadCandidatePath(document, includeContext);
            if (result is not null) return result;
        }

        return null;
    }

    private static WorkerResponse? ReadCandidatePath(AutomationElement candidate, bool includeContext)
    {
        var current = candidate;
        for (var depth = 0; current is not null && depth < MaximumAncestorDepth; depth++)
        {
            if (IsPassword(current)) return Empty("password-control", SelectionFailureKind.ProtectedContent);
            if (TryReadText(current, includeContext, out var text, out var bounds, out var context))
            {
                return new WorkerResponse(
                    text,
                    SelectionFailureKind.None,
                    "uia-text-pattern",
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    context);
            }

            try { current = TreeWalker.RawViewWalker.GetParent(current); }
            catch (Exception exception) when (IsRecoverable(exception)) { break; }
        }

        return null;
    }

    private static bool TryReadText(
        AutomationElement element,
        bool includeContext,
        out string text,
        out Rect bounds,
        out string? context)
    {
        text = string.Empty;
        bounds = default;
        context = null;
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject)
                || patternObject is not TextPattern pattern)
                return false;

            var ranges = pattern.GetSelection();
            if (ranges is null || ranges.Length == 0) return false;

            var parts = new List<string>(ranges.Length);
            var selectionBounds = Rect.Empty;
            var remaining = MaximumTextLength;
            foreach (var range in ranges)
            {
                if (remaining <= 0) break;
                var value = Normalize(range.GetText(remaining));
                if (value.Length == 0) continue;
                parts.Add(value);
                remaining -= value.Length;
                var rectangles = range.GetBoundingRectangles();
                foreach (var rectangle in rectangles)
                {
                    if (rectangle.IsEmpty || !double.IsFinite(rectangle.X) || !double.IsFinite(rectangle.Y)
                        || !double.IsFinite(rectangle.Width) || !double.IsFinite(rectangle.Height)
                        || rectangle.Width <= 0 || rectangle.Height <= 0) continue;
                    selectionBounds.Union(rectangle);
                }
                if (includeContext && context is null)
                {
                    var paragraph = range.Clone();
                    paragraph.ExpandToEnclosingUnit(TextUnit.Paragraph);
                    context = Normalize(paragraph.GetText(4000));
                }
            }

            if (parts.Count == 0) return false;
            text = string.Join(Environment.NewLine, parts);
            bounds = selectionBounds.IsEmpty ? default : selectionBounds;
            if (double.IsNaN(bounds.X) || double.IsNaN(bounds.Y)
                || double.IsNaN(bounds.Width) || double.IsNaN(bounds.Height))
            {
                bounds = default;
            }

            return true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    private static IEnumerable<AutomationElement> FindVisibleDocumentCandidates(
        IntPtr expectedRootOwner,
        Point point)
    {
        AutomationElement root;
        try { root = AutomationElement.FromHandle(expectedRootOwner); }
        catch (Exception exception) when (IsRecoverable(exception)) { yield break; }

        var pending = new PriorityQueue<DocumentCandidate, (int Rank, int Order)>();
        var examined = 0;

        void Enqueue(AutomationElement node, int depth)
        {
            if (++examined > MaximumDocumentNodes || depth >= MaximumAncestorDepth) return;
            if (!TryGetDocumentInfo(node, point, out var info)) return;

            var rank = info.IsDocument && info.ContainsPointer
                ? 0
                : info.ContainsPointer
                    ? 1
                    : info.IsDocument
                        ? 2
                        : info.IsOffscreen ? 4 : 3;
            pending.Enqueue(new DocumentCandidate(node, depth, info), (rank, examined));
        }

        Enqueue(root, 0);
        while (pending.TryDequeue(out var item, out _))
        {
            if (item.Info.IsDocument && (!item.Info.IsOffscreen || item.Info.ContainsPointer))
                yield return item.Element;

            // Do not expand an offscreen branch unless the point is inside it;
            // this avoids walking every page in a PDF document tree.
            if ((item.Info.IsOffscreen && !item.Info.ContainsPointer)
                || examined >= MaximumDocumentNodes)
                continue;

            AutomationElement? child;
            try { child = TreeWalker.RawViewWalker.GetFirstChild(item.Element); }
            catch (Exception exception) when (IsRecoverable(exception)) { continue; }

            while (child is not null && examined < MaximumDocumentNodes)
            {
                Enqueue(child, item.Depth + 1);
                try { child = TreeWalker.RawViewWalker.GetNextSibling(child); }
                catch (Exception exception) when (IsRecoverable(exception)) { break; }
            }
        }
    }

    private static bool TryGetDocumentInfo(
        AutomationElement element,
        Point point,
        out DocumentNodeInfo info)
    {
        info = default;
        try
        {
            var state = element.Current;
            var bounds = state.BoundingRectangle;
            info = new DocumentNodeInfo(
                state.ControlType == ControlType.Document,
                state.IsOffscreen,
                bounds.Contains(point),
                state.IsPassword);
            return !info.IsPassword;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    private static bool ShouldIncludeFocusedElement(
        AutomationElement element,
        uint hitProcessId,
        IntPtr expectedRootOwner,
        bool allowEmbeddedProcess)
    {
        try
        {
            var focusedProcessId = (uint)element.Current.ProcessId;
            if (allowEmbeddedProcess && focusedProcessId != 0
                && expectedRootOwner != IntPtr.Zero
                && expectedRootOwner == TryGetRootOwnerHandle(element))
                return true;

            if (focusedProcessId != hitProcessId) return false;
            var focusedRoot = TryGetRootOwnerHandle(element);
            return focusedRoot == IntPtr.Zero || expectedRootOwner == IntPtr.Zero || focusedRoot == expectedRootOwner;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    private static IntPtr TryGetRootOwnerHandle(AutomationElement? element)
    {
        var current = element;
        for (var depth = 0; current is not null && depth < MaximumAncestorDepth; depth++)
        {
            try
            {
                var handle = new IntPtr(current.Current.NativeWindowHandle);
                if (handle != IntPtr.Zero) return GetRootOwner(handle);
                current = TreeWalker.RawViewWalker.GetParent(current);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                return IntPtr.Zero;
            }
        }

        return IntPtr.Zero;
    }

    private static IntPtr GetRootOwnerAt(Point point)
    {
        var hit = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint
        {
            X = (int)Math.Round(point.X),
            Y = (int)Math.Round(point.Y),
        });
        return hit == IntPtr.Zero ? IntPtr.Zero : GetRootOwner(hit);
    }

    private static IntPtr GetRootOwner(IntPtr window)
    {
        var root = NativeMethods.GetAncestor(window, GaRootOwner);
        return root != IntPtr.Zero ? root : NativeMethods.GetAncestor(window, GaRoot);
    }

    private static bool IsDocumentHostProcess(uint processId, IntPtr rootWindow)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            if (process.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("chromium", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("brave", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("firefox", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("zotero", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("wpspdf", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!process.ProcessName.Equals("wps", StringComparison.OrdinalIgnoreCase)) return false;
            var title = new StringBuilder(512);
            return NativeMethods.GetWindowText(rootWindow, title, title.Capacity) > 0
                && title.ToString().Contains("pdf", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or OverflowException)
        {
            return false;
        }
    }

    private static bool ActivateEdgeAccessibility(Point point, bool refresh)
    {
        var hitWindow = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint
        {
            X = (int)Math.Round(point.X),
            Y = (int)Math.Round(point.Y),
        });
        var targetWindow = hitWindow == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.GetAncestor(hitWindow, GaRoot);
        if (targetWindow == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(targetWindow, out var processId);
        if (processId == 0 || !IsProcessNamed(processId, "msedge")) return false;

        lock (EdgeActivationSync)
        {
            var now = Environment.TickCount64;
            var cooldown = refresh ? EdgeRefreshCooldownMilliseconds : EdgeActivationLifetimeMilliseconds;
            var key = (processId, targetWindow);
            if (EdgeActivations.TryGetValue(key, out var last)
                && now >= last && now - last < cooldown)
                return false;

            var originalState = 0;
            if (!NativeMethods.SystemParametersInfoGet(
                    SpiGetScreenReader,
                    0,
                    ref originalState,
                    0)
                || originalState != 0)
                return false;

            if (!NativeMethods.SystemParametersInfoSet(
                    SpiSetScreenReader,
                    1,
                    IntPtr.Zero,
                    SpifSendChange))
                return false;

            try
            {
                Thread.Sleep(EdgeActivationPulseMilliseconds);
                EdgeActivations[key] = now;
                if (EdgeActivations.Count > 64)
                {
                    var oldest = EdgeActivations.MinBy(entry => entry.Value).Key;
                    EdgeActivations.Remove(oldest);
                }
                return true;
            }
            finally
            {
                // Restore the exact state observed before the pulse.
                _ = NativeMethods.SystemParametersInfoSet(
                    SpiSetScreenReader,
                    (uint)originalState,
                    IntPtr.Zero,
                    SpifSendChange);
            }
        }
    }

    private static bool IsProcessNamed(uint processId, string expectedName)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName.Equals(expectedName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or OverflowException)
        {
            return false;
        }
    }

    private static bool IsPassword(AutomationElement element)
    {
        try { return element.Current.IsPassword; }
        catch (Exception exception) when (IsRecoverable(exception)) { return false; }
    }

    private static void TryGet(Func<AutomationElement> factory, Action<AutomationElement> assign)
    {
        try
        {
            var value = factory();
            if (value is not null) assign(value);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
        }
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    private static WorkerResponse Empty(
        string code,
        SelectionFailureKind failure = SelectionFailureKind.Empty) =>
        new(null, failure, code, null, null, null, null);

    private static int? ReadParentId(string[] args)
    {
        var index = Array.FindIndex(args, static value =>
            string.Equals(value, "--parent-pid", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var id)
            ? id
            : null;
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is ElementNotAvailableException
            or InvalidOperationException
            or COMException
            or System.ComponentModel.Win32Exception
            or ArgumentException
            or NotSupportedException
            or TimeoutException;

    private static async Task WaitForParentAsync(Process parent)
    {
        try { await parent.WaitForExitAsync().ConfigureAwait(false); }
        catch (InvalidOperationException) { }
        finally
        {
            parent.Dispose();
            Environment.Exit(0);
        }
    }

    private readonly record struct DocumentNodeInfo(
        bool IsDocument,
        bool IsOffscreen,
        bool ContainsPointer,
        bool IsPassword);

    private readonly record struct DocumentCandidate(
        AutomationElement Element,
        int Depth,
        DocumentNodeInfo Info);

    private sealed record WorkerRequest(double X, double Y, string? Trigger, bool IncludeContext = false);

    private sealed record WorkerResponse(
        string? Text,
        SelectionFailureKind Failure,
        string? DiagnosticCode,
        double? BoundsX,
        double? BoundsY,
        double? BoundsWidth,
        double? BoundsHeight,
        string? Context = null);

    private static class NativeMethods
    {
        internal const uint CoInitMultithreaded = 0x0;

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint
        {
            internal int X;
            internal int Y;
        }

        [DllImport("ole32.dll")]
        internal static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll")]
        internal static extern void CoUninitialize();

        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr window, StringBuilder text, int length);

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
    }
}
