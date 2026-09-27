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

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
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

        var point = new Point(request.X, request.Y);
        var candidates = new List<AutomationElement>();
        TryAdd(candidates, () => AutomationElement.FromPoint(point));
        TryAdd(candidates, () => AutomationElement.FocusedElement);

        foreach (var candidate in candidates)
        {
            var current = candidate;
            for (var depth = 0; current is not null && depth < MaximumAncestorDepth; depth++)
            {
                if (IsPassword(current)) return Empty("password-control");
                if (TryReadText(current, out var text, out var bounds))
                {
                    return new WorkerResponse(
                        text,
                        SelectionFailureKind.None,
                        "uia-text-pattern",
                        bounds.X,
                        bounds.Y,
                        bounds.Width,
                        bounds.Height);
                }

                try { current = TreeWalker.RawViewWalker.GetParent(current); }
                catch (Exception exception) when (IsRecoverable(exception)) { break; }
            }
        }

        return Empty("uia-selection-empty");
    }

    private static bool TryReadText(
        AutomationElement element,
        out string text,
        out Rect bounds)
    {
        text = string.Empty;
        bounds = default;
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject)
                || patternObject is not TextPattern pattern)
                return false;

            var ranges = pattern.GetSelection();
            if (ranges is null || ranges.Length == 0) return false;

            var parts = new List<string>(ranges.Length);
            var remaining = MaximumTextLength;
            foreach (var range in ranges)
            {
                if (remaining <= 0) break;
                var value = Normalize(range.GetText(remaining));
                if (value.Length == 0) continue;
                parts.Add(value);
                remaining -= value.Length;
            }

            if (parts.Count == 0) return false;
            bounds = element.Current.BoundingRectangle;
            return true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    private static bool IsPassword(AutomationElement element)
    {
        try { return element.Current.IsPassword; }
        catch (Exception exception) when (IsRecoverable(exception)) { return false; }
    }

    private static void TryAdd(List<AutomationElement> candidates, Func<AutomationElement> factory)
    {
        try
        {
            var value = factory();
            if (value is not null && !candidates.Contains(value)) candidates.Add(value);
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

    private static WorkerResponse Empty(string code) =>
        new(null, SelectionFailureKind.Empty, code, null, null, null, null);

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
            or NotSupportedException;

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

    private sealed record WorkerRequest(double X, double Y, string? Trigger);

    private sealed record WorkerResponse(
        string? Text,
        SelectionFailureKind Failure,
        string? DiagnosticCode,
        double? BoundsX,
        double? BoundsY,
        double? BoundsWidth,
        double? BoundsHeight);
}
