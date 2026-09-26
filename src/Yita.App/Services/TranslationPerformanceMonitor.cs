using System.Globalization;

namespace Yita.Services;

internal enum TranslationTrigger
{
    Selection,
    Clipboard,
    ZoteroIntegration,
    Retranslation,
}

internal enum TranslationOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    NoSelection,
}

internal sealed record TranslationPerformanceSample(
    DateTimeOffset CompletedAt,
    TranslationTrigger Trigger,
    string ProviderId,
    TranslationOutcome Outcome,
    bool CacheHit,
    bool Coalesced,
    double? SelectionReadMilliseconds,
    double? QueueMilliseconds,
    double? FirstContentMilliseconds,
    double TotalMilliseconds);

/// <summary>
/// Keeps a small process-local ring of timing numbers. It deliberately accepts
/// no source text, translation text, endpoint, model, or API credential.
/// </summary>
internal sealed class TranslationPerformanceMonitor
{
    internal const int DefaultCapacity = 100;

    private readonly object _syncRoot = new();
    private readonly int _capacity;
    private readonly Queue<TranslationPerformanceSample> _samples = new();

    public TranslationPerformanceMonitor(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public TranslationPerformanceOperation Begin(
        TranslationTrigger trigger,
        string? providerId)
    {
        return new TranslationPerformanceOperation(
            this,
            trigger,
            string.IsNullOrWhiteSpace(providerId) ? "unknown" : providerId.Trim());
    }

    public IReadOnlyList<TranslationPerformanceSample> Snapshot()
    {
        lock (_syncRoot)
        {
            return _samples.ToArray();
        }
    }

    public string CreateReport(bool useChinese)
    {
        var samples = Snapshot();
        if (samples.Count == 0)
        {
            return useChinese
                ? "Yita 性能诊断\n尚无翻译请求数据。\n报告不包含原文、译文、API Key 或 Endpoint。"
                : "Yita performance diagnostics\nNo translation request data yet.\nThe report contains no source text, translations, API key, or endpoint.";
        }

        var succeeded = samples.Count(sample => sample.Outcome == TranslationOutcome.Succeeded);
        var failed = samples.Count(sample => sample.Outcome == TranslationOutcome.Failed);
        var cancelled = samples.Count(sample => sample.Outcome == TranslationOutcome.Cancelled);
        var noSelection = samples.Count(sample => sample.Outcome == TranslationOutcome.NoSelection);
        var cacheHits = samples.Count(sample => sample.CacheHit);
        var coalesced = samples.Count(sample => sample.Coalesced);
        var selection = samples
            .Where(sample => sample.SelectionReadMilliseconds.HasValue)
            .Select(sample => sample.SelectionReadMilliseconds!.Value)
            .ToArray();
        var queue = samples
            .Where(sample => sample.QueueMilliseconds.HasValue)
            .Select(sample => sample.QueueMilliseconds!.Value)
            .ToArray();
        var firstContent = samples
            .Where(sample => sample.FirstContentMilliseconds.HasValue)
            .Select(sample => sample.FirstContentMilliseconds!.Value)
            .ToArray();
        var total = samples.Select(sample => sample.TotalMilliseconds).ToArray();

        var lines = useChinese
            ? new List<string>
            {
                "Yita 性能诊断",
                $"样本：{samples.Count}（成功 {succeeded} / 失败 {failed} / 取消 {cancelled}）",
                $"未读取到选中文字：{noSelection}（尚未请求翻译或显示译文窗）",
                $"缓存命中：{Percent(cacheHits, samples.Count)} · 合并重复请求：{coalesced}",
                FormatPercentiles("读取选区", selection, "毫秒"),
                FormatPercentiles("请求排队", queue, "毫秒"),
                FormatPercentiles("首段译文", firstContent, "毫秒"),
                FormatPercentiles("总耗时", total, "毫秒"),
                "报告不包含原文、译文、API Key、Endpoint 或文件路径。",
            }
            : new List<string>
            {
                "Yita performance diagnostics",
                $"Samples: {samples.Count} (success {succeeded} / failed {failed} / cancelled {cancelled})",
                $"No selection read: {noSelection} (before translation or popup display)",
                $"Cache hits: {Percent(cacheHits, samples.Count)} · Coalesced duplicates: {coalesced}",
                FormatPercentiles("Selection read", selection, "ms"),
                FormatPercentiles("Request queue", queue, "ms"),
                FormatPercentiles("First content", firstContent, "ms"),
                FormatPercentiles("Total", total, "ms"),
                "The report contains no source text, translations, API key, endpoint, or file path.",
            };

        return string.Join(Environment.NewLine, lines);
    }

    internal void Add(TranslationPerformanceSample sample)
    {
        lock (_syncRoot)
        {
            while (_samples.Count >= _capacity)
            {
                _samples.Dequeue();
            }

            _samples.Enqueue(sample);
        }
    }

    internal static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        var rank = Math.Max(1, (int)Math.Ceiling(percentile * sorted.Length));
        return sorted[Math.Min(rank, sorted.Length) - 1];
    }

    private static string FormatPercentiles(
        string name,
        IReadOnlyCollection<double> values,
        string unit)
    {
        return values.Count == 0
            ? $"{name}: n/a"
            : $"{name}: P50 {Format(Percentile(values, 0.50))} {unit} · P95 {Format(Percentile(values, 0.95))} {unit}";
    }

    private static string Percent(int count, int total)
    {
        return total == 0
            ? "0%"
            : Math.Round((double)count / total * 100)
                .ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    private static string Format(double value) =>
        Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);
}

internal sealed class TranslationPerformanceOperation
{
    private readonly TranslationPerformanceMonitor _monitor;
    private readonly TranslationTrigger _trigger;
    private readonly string _providerId;
    private readonly long _startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    private long? _translationStartedAt;
    private long? _queueStartedAt;
    private double? _selectionReadMilliseconds;
    private double? _queueMilliseconds;
    private double? _firstContentMilliseconds;
    private bool _cacheHit;
    private bool _coalesced;
    private int _completed;

    internal TranslationPerformanceOperation(
        TranslationPerformanceMonitor monitor,
        TranslationTrigger trigger,
        string providerId)
    {
        _monitor = monitor;
        _trigger = trigger;
        _providerId = providerId;
    }

    public void MarkSelectionRead(TimeSpan elapsed) =>
        _selectionReadMilliseconds = Math.Max(0, elapsed.TotalMilliseconds);

    public void MarkTranslationStarted()
    {
        _translationStartedAt ??= System.Diagnostics.Stopwatch.GetTimestamp();
    }

    public void MarkQueueStarted()
    {
        MarkTranslationStarted();
        _queueStartedAt ??= System.Diagnostics.Stopwatch.GetTimestamp();
    }

    public void MarkQueueCompleted()
    {
        if (_queueStartedAt is { } startedAt)
        {
            _queueMilliseconds ??= ElapsedMilliseconds(startedAt);
        }
    }

    public void MarkFirstContent()
    {
        if (_translationStartedAt is { } startedAt)
        {
            _firstContentMilliseconds ??= ElapsedMilliseconds(startedAt);
        }
    }

    public void MarkCacheHit() => _cacheHit = true;

    public void MarkCoalesced() => _coalesced = true;

    public void Complete(TranslationOutcome outcome)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        _monitor.Add(new TranslationPerformanceSample(
            DateTimeOffset.UtcNow,
            _trigger,
            _providerId,
            outcome,
            _cacheHit,
            _coalesced,
            _selectionReadMilliseconds,
            _queueMilliseconds,
            _firstContentMilliseconds,
            ElapsedMilliseconds(_startedAt)));
    }

    private static double ElapsedMilliseconds(long startedAt) =>
        System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
}
