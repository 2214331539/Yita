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
            await store.SaveAsync(YitaSettings.Default with
            {
                TargetLanguage = "English",
                AiHistoryEnabled = true,
                PopupOffsetX = -24.5,
                PopupOffsetY = 36,
            });
            var loaded = await store.LoadAsync();

            Assert.Equal("English", loaded.TargetLanguage);
            Assert.True(loaded.AiHistoryEnabled);
            Assert.Equal(-24.5, loaded.PopupOffsetX);
            Assert.Equal(36, loaded.PopupOffsetY);
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
    public void SelectionGestureDetectorIgnoresClicksAndReportsDragBounds()
    {
        var detector = new SelectionGestureDetector(4, 4);
        detector.Press(new ScreenPoint(140, 280));

        Assert.Null(detector.Release(new ScreenPoint(142, 282), DateTimeOffset.UtcNow));

        detector.Press(new ScreenPoint(140, 280));
        var gesture = detector.Release(new ScreenPoint(60, 220), DateTimeOffset.UtcNow);

        Assert.True(gesture.HasValue);
        Assert.Equal(new SelectionBounds(60, 220, 80, 60), gesture.Value.Bounds);
        Assert.Equal(new ScreenPoint(60, 280), gesture.Value.PopupAnchor);
    }

    [Fact]
    public void SelectionRequestCarriesGestureBoundsForPopupPlacement()
    {
        var bounds = new SelectionBounds(20, 40, 100, 18);
        var request = new SelectionRequest(
            SelectionTrigger.MouseGesture,
            new ScreenPoint(120, 58),
            GestureBounds: bounds);

        Assert.Equal(bounds, request.GestureBounds);
    }

    [Fact]
    public async Task SelectionPipelineFallsThroughReaderFailures()
    {
        var pipeline = new SelectionReaderPipeline(new ISelectionReader[]
        {
            new StubSelectionReader(SelectionResult.Failed(SelectionFailureKind.Empty, "empty")),
            new StubSelectionReader(new SelectionResult("found", SelectionSource.Accessibility)),
        });

        var result = await pipeline.ReadAsync(new SelectionRequest(
            SelectionTrigger.TranslateShortcut, new ScreenPoint(10, 20)));

        Assert.True(result.Succeeded);
        Assert.Equal("found", result.Text);
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

    [Fact]
    public async Task CoordinatorCachesCompletedTranslationAndWritesHistory()
    {
        var translator = new StubTranslator("你好");
        var cache = new MemoryTranslationCache();
        var historyPath = Path.Combine(Path.GetTempPath(), "yita-coordinator-" + Guid.NewGuid().ToString("N"), "history.jsonl");
        var history = new JsonlTranslationHistoryStore(historyPath);
        try
        {
            var coordinator = new TranslationCoordinator(translator, cache, history);
            var first = await ReadAllAsync(coordinator.TranslateAsync(new TranslationRequest("  hello  ")));
            var second = await ReadAllAsync(coordinator.TranslateAsync(new TranslationRequest("hello")));

            Assert.Equal("你好", first);
            Assert.Equal("你好", second);
            Assert.Equal(1, translator.Calls);
            Assert.Single(await ReadHistoryAsync(history));
        }
        finally
        {
            var directory = Path.GetDirectoryName(historyPath);
            if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static async Task<string> ReadAllAsync(IAsyncEnumerable<TranslationChunk> chunks)
    {
        var result = new System.Text.StringBuilder();
        await foreach (var chunk in chunks) result.Append(chunk.TextDelta);
        return result.ToString();
    }

    private static async Task<List<TranslationHistoryEntry>> ReadHistoryAsync(ITranslationHistory history)
    {
        var entries = new List<TranslationHistoryEntry>();
        await foreach (var entry in history.ReadAsync()) entries.Add(entry);
        return entries;
    }

    private sealed class StubTranslator(string result) : IStreamingTranslator
    {
        public int Calls { get; private set; }

        public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
            TranslationRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            yield return new TranslationChunk(result);
            yield return new TranslationChunk(string.Empty, true);
        }
    }

    private sealed class StubSelectionReader(SelectionResult result) : ISelectionReader
    {
        public Task<SelectionResult> ReadAsync(SelectionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
}
