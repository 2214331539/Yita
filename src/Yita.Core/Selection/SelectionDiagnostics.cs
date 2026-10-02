using System.Globalization;

namespace Yita.Core.Selection;

public enum SelectionIssue
{
    Succeeded, Empty, Cancelled, PermissionDenied, ComponentMissing, VersionMismatch,
    RestartBackoff, Timeout, ProtectedContent, UnsafeCopy, ClipboardChanged,
    ClipboardUnavailable, TextLimit, UnsupportedApplication, Unknown,
}

public static class SelectionIssueClassifier
{
    public static bool IsActionable(SelectionIssue issue) => issue is not
        (SelectionIssue.Succeeded or SelectionIssue.Empty or SelectionIssue.Cancelled);

    public static SelectionIssue Classify(SelectionResult result)
    {
        if (result.Succeeded) return SelectionIssue.Succeeded;
        var code = result.DiagnosticCode;
        if (code?.StartsWith("mac-helper-", StringComparison.Ordinal) == true) code = code[11..];
        return code switch
        {
            "missing" or "uia-worker-missing" => SelectionIssue.ComponentMissing,
            "version-mismatch" => SelectionIssue.VersionMismatch,
            "restart-backoff" => SelectionIssue.RestartBackoff,
            "ax-text-limit" or "clipboard-text-limit" => SelectionIssue.TextLimit,
            "manual-clipboard-empty" or "clipboard-no-text" => SelectionIssue.Empty,
            "clipboard-changed-by-user" or "clipboard-owner-changed" or "clipboard-changed-during-read"
                or "clipboard-superseded" => SelectionIssue.ClipboardChanged,
            "clipboard-cannot-preserve" or "clipboard-snapshot-unavailable" or "copy-terminal-blocked"
                or "copy-user-keys-held" or "clipboard-unsafe-target" => SelectionIssue.UnsafeCopy,
            "copy-password-control" or "ax-protected-content" => SelectionIssue.ProtectedContent,
            "copy-input-rejected" or "clipboard-permission-denied" or "ax-permission-denied"
                or "permission-denied" => SelectionIssue.PermissionDenied,
            _ => result.Failure switch
            {
                SelectionFailureKind.None or SelectionFailureKind.Empty => SelectionIssue.Empty,
                SelectionFailureKind.Cancelled => SelectionIssue.Cancelled,
                SelectionFailureKind.PermissionDenied => SelectionIssue.PermissionDenied,
                SelectionFailureKind.Timeout => SelectionIssue.Timeout,
                SelectionFailureKind.ProtectedContent => SelectionIssue.ProtectedContent,
                SelectionFailureKind.ClipboardUnavailable => SelectionIssue.ClipboardUnavailable,
                SelectionFailureKind.UnsupportedApplication => SelectionIssue.UnsupportedApplication,
                _ => SelectionIssue.Unknown,
            },
        };
    }
}

public sealed record SelectionDiagnosticSample(SelectionTrigger Trigger, SelectionIssue Issue,
    SelectionSource? Source, double? ReadMilliseconds);

// Only typed outcomes and timings cross this boundary; no request or result objects are retained.
public sealed class SelectionDiagnostics
{
    public const int SampleCapacity = 128;
    private readonly object _gate = new();
    private readonly Queue<SelectionDiagnosticSample> _samples = new();
    private readonly long[] _counts = new long[Enum.GetValues<SelectionIssue>().Length];
    private SelectionDiagnosticSample? _lastMeaningful;

    public SelectionDiagnosticSample Record(SelectionTrigger trigger, SelectionResult result, TimeSpan? duration = null)
    {
        var milliseconds = duration?.TotalMilliseconds;
        var sample = new SelectionDiagnosticSample(trigger, SelectionIssueClassifier.Classify(result),
            result.Succeeded ? result.Source : null,
            milliseconds is >= 0 and <= 300_000 ? milliseconds : null);
        lock (_gate)
        {
            _counts[(int)sample.Issue]++;
            if (_samples.Count == SampleCapacity) _samples.Dequeue();
            _samples.Enqueue(sample);
            if (sample.Issue is not (SelectionIssue.Empty or SelectionIssue.Cancelled)) _lastMeaningful = sample;
        }
        return sample;
    }

    public SelectionDiagnosticSample? LastMeaningful { get { lock (_gate) return _lastMeaningful; } }
    public IReadOnlyList<SelectionDiagnosticSample> Samples { get { lock (_gate) return _samples.ToArray(); } }

    public string CreateReport(bool chinese)
    {
        SelectionDiagnosticSample[] samples;
        long[] counts;
        lock (_gate) { samples = _samples.ToArray(); counts = (long[])_counts.Clone(); }
        var lines = new List<string>
        {
            chinese ? "取词诊断（本次运行；不含正文或来源应用）" : "Selection diagnostics (this session; no text or source apps)",
            (chinese ? "已完成读取：" : "Completed reads: ") + counts.Sum().ToString(CultureInfo.InvariantCulture),
            (chinese ? "最近样本：" : "Recent samples: ") + samples.Length + "/" + SampleCapacity,
        };
        foreach (var issue in Enum.GetValues<SelectionIssue>())
            if (counts[(int)issue] > 0) lines.Add(issue + "=" + counts[(int)issue].ToString(CultureInfo.InvariantCulture));
        foreach (var source in Enum.GetValues<SelectionSource>())
        {
            var values = samples.Where(sample => sample.Source == source && sample.ReadMilliseconds.HasValue)
                .Select(sample => sample.ReadMilliseconds!.Value).Order().ToArray();
            if (values.Length == 0) continue;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{source}: n={values.Length}; p50={Percentile(values, .5):0.0}ms; p95={Percentile(values, .95):0.0}ms"));
        }
        lines.Add(chinese ? "耗时仅含取词，不含触发等待或网络；统计不代表应用兼容率。" : "Timing excludes gesture delay and network; counts do not establish app compatibility.");
        return string.Join("\n", lines);
    }

    private static double Percentile(double[] values, double percentile) => values[(int)Math.Ceiling(values.Length * percentile) - 1];
}
