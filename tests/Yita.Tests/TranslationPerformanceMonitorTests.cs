using Yita.Services;

namespace Yita.Tests;

public sealed class TranslationPerformanceMonitorTests
{
    [Fact]
    public void MissingSelectionIsReportedSeparatelyFromCancellationAndApiFailure()
    {
        var monitor = new TranslationPerformanceMonitor();
        monitor.Begin(TranslationTrigger.Selection, "deepseek").Complete(TranslationOutcome.NoSelection);
        var report = monitor.CreateReport(useChinese: false);
        Assert.Contains("No selection read: 1", report, StringComparison.Ordinal);
        Assert.Contains("cancelled 0", report, StringComparison.Ordinal);
        Assert.Contains("failed 0", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_IsBoundedAndContainsNoTextFields()
    {
        var monitor = new TranslationPerformanceMonitor(capacity: 2);
        for (var index = 0; index < 3; index++)
        {
            var operation = monitor.Begin(TranslationTrigger.Selection, "deepseek");
            operation.MarkSelectionRead(TimeSpan.FromMilliseconds(index + 1));
            operation.MarkTranslationStarted();
            operation.MarkFirstContent();
            operation.Complete(TranslationOutcome.Succeeded);
        }

        var snapshot = monitor.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.DoesNotContain(
            typeof(TranslationPerformanceSample).GetProperties(),
            property => property.Name.Contains("Text", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Endpoint", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateReport_SummarizesCacheCoalescingAndPercentiles()
    {
        var monitor = new TranslationPerformanceMonitor();
        for (var index = 1; index <= 20; index++)
        {
            var operation = monitor.Begin(TranslationTrigger.Selection, "deepseek");
            operation.MarkSelectionRead(TimeSpan.FromMilliseconds(index));
            if (index <= 5)
            {
                operation.MarkCacheHit();
            }

            if (index <= 2)
            {
                operation.MarkCoalesced();
            }

            operation.Complete(TranslationOutcome.Succeeded);
        }

        var report = monitor.CreateReport(useChinese: false);

        Assert.Contains("Samples: 20", report, StringComparison.Ordinal);
        Assert.Contains("Cache hits: 25%", report, StringComparison.Ordinal);
        Assert.Contains("Coalesced duplicates: 2", report, StringComparison.Ordinal);
        Assert.Contains("P50 10", report, StringComparison.Ordinal);
        Assert.Contains("P95 19", report, StringComparison.Ordinal);
        Assert.Contains("no source text", report, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0.50, 2)]
    [InlineData(0.95, 4)]
    public void Percentile_UsesNearestRank(double percentile, double expected)
    {
        Assert.Equal(
            expected,
            TranslationPerformanceMonitor.Percentile([1, 2, 3, 4], percentile));
    }
}
