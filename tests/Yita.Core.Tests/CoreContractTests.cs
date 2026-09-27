using Yita.Core.Placement;
using Yita.Core.Selection;
using Yita.Core.Settings;
using Yita.Core.Translation;

namespace Yita.Core.Tests;

public sealed class CoreContractTests
{
    [Fact]
    public void PopupOffsetIsRelativeToTheCurrentSelectionAnchor()
    {
        var first = PopupPlacementResolver.Resolve(
            new SelectionAnchor(100, 200),
            new PopupSize(240, 120),
            new WorkArea(0, 0, 1200, 800),
            new PopupOffset(18, 24));
        var second = PopupPlacementResolver.Resolve(
            new SelectionAnchor(700, 400),
            new PopupSize(240, 120),
            new WorkArea(0, 0, 1200, 800),
            new PopupOffset(18, 24));

        Assert.Equal((118d, 224d), first);
        Assert.Equal((718d, 424d), second);
    }

    [Fact]
    public async Task SettingsStoreRoundTripsWithoutCredentials()
    {
        var path = Path.Combine(Path.GetTempPath(), "yita-core-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new JsonSettingsStore(path);
            await store.SaveAsync(YitaSettings.Default with { TargetLanguage = "English", AiHistoryEnabled = true });
            var loaded = await store.LoadAsync();

            Assert.Equal("English", loaded.TargetLanguage);
            Assert.True(loaded.AiHistoryEnabled);
        }
        finally
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void SelectionResultDistinguishesPermissionFailure()
    {
        var result = SelectionResult.Failed(SelectionFailureKind.PermissionDenied, "accessibility-required");

        Assert.False(result.Succeeded);
        Assert.Equal(SelectionFailureKind.PermissionDenied, result.Failure);
        Assert.Equal("accessibility-required", result.DiagnosticCode);
    }

    [Fact]
    public void TranslationCacheSeparatesLanguagePairs()
    {
        var cache = new MemoryTranslationCache(2);
        cache.Set("hello", "en", "zh-CN", "你好");

        Assert.True(cache.TryGet("hello", "en", "zh-CN", out var translation));
        Assert.Equal("你好", translation);
        Assert.False(cache.TryGet("hello", "en", "ja", out _));
    }

    [Fact]
    public async Task HistoryStoreReadsJsonLinesAndSkipsBrokenRecords()
    {
        var directory = Path.Combine(Path.GetTempPath(), "yita-history-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "history.jsonl");
        try
        {
            var store = new JsonlTranslationHistoryStore(path);
            await store.AppendAsync(new TranslationHistoryEntry(
                DateTimeOffset.UtcNow, "hello", "你好", "en", "zh-CN"));
            await File.AppendAllTextAsync(path, "not-json\n");

            var entries = new List<TranslationHistoryEntry>();
            await foreach (var entry in store.ReadAsync()) entries.Add(entry);

            var only = Assert.Single(entries);
            Assert.Equal("hello", only.SourceText);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
