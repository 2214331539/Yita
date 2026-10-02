using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Yita.Core.Placement;
using Yita.Core.Settings;
using Yita.Core.Translation;

namespace Yita.Core.Tests;

public sealed class TranslationReliabilityTests
{
    [Fact]
    public void NewRequestInvalidatesOldWorkAndDisposingOldWorkPreservesNewRequest()
    {
        using var controller = new LatestRequestController();
        var old = controller.Begin();
        var token = old.Token;
        using var current = controller.Begin();
        Assert.True(token.IsCancellationRequested);
        Assert.False(old.IsCurrent);
        old.Dispose();
        Assert.True(current.IsCurrent);
        controller.Cancel();
        Assert.False(current.IsCurrent);
        Assert.True(current.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void DragOffsetFollowsNewSelectionAtDifferentDpi(double scale)
    {
        var offset = PopupPlacementResolver.CaptureLogicalOffset(new SelectionAnchor(100, 200), 145, 260, 1.5);
        Assert.Equal(new PopupOffset(30, 40), offset);
        var next = PopupPlacementResolver.ResolveForScale(new SelectionAnchor(500, 400),
            new PopupSize(280, 120), new WorkArea(0, 0, 2000, 1500), scale, offset);
        Assert.Equal((500 + 30 * scale, 400 + 40 * scale), next);
    }

    [Fact]
    public void PopupPositionIsClampedToASecondaryMonitor()
    {
        var location = PopupPlacementResolver.ResolveForScale(new SelectionAnchor(-50, 700),
            new PopupSize(380, 200), new WorkArea(-1920, 0, 1920, 1080), 1.5, new PopupOffset(20, 20));
        Assert.True(location.X >= -1912 && location.X + 570 <= -8);
        Assert.True(location.Y >= 8 && location.Y + 300 <= 1072);
    }

    [Fact]
    public void CacheSeparatesPromptSettingsAndEvictsWithoutDuplicateQueueEntries()
    {
        var request = new TranslationRequest("hello");
        var cache = new MemoryTranslationCache(2);
        for (var index = 0; index < 100; index++) cache.Set(request, "你好");
        Assert.False(cache.TryGet(request with { Mode = "literal" }, out _));
        Assert.False(cache.TryGet(request with { Context = "medical" }, out _));
        Assert.False(cache.TryGet(request with { PersonalGlossary = "hello=嗨" }, out _));
        cache.Set(request with { Text = "second" }, "第二");
        cache.Set(request with { Text = "third" }, "第三");
        Assert.False(cache.TryGet(request, out _));
        Assert.True(cache.TryGet(request with { Text = "second" }, out _));
        Assert.True(cache.TryGet(request with { Text = "third" }, out _));
    }

    [Fact]
    public async Task IncompleteAnswerIsNeverCached()
    {
        var cache = new MemoryTranslationCache();
        var coordinator = new TranslationCoordinator(new PartialTranslator(), cache);
        var exception = await Assert.ThrowsAsync<TranslationProviderException>(async () =>
        {
            await foreach (var _ in coordinator.TranslateAsync(new TranslationRequest("hello"))) { }
        });
        Assert.Equal(TranslationFailureKind.Protocol, exception.Kind);
        Assert.False(cache.TryGet(new TranslationRequest("hello"), out _));
    }

    [Fact]
    public async Task HistoryFailureDoesNotFailACompletedTranslation()
    {
        var coordinator = new TranslationCoordinator(new CompleteTranslator(), history: new FailingHistory());
        Exception? reported = null;
        coordinator.HistoryWriteFailed += exception => reported = exception;
        var text = string.Empty;
        await foreach (var chunk in coordinator.TranslateAsync(new TranslationRequest("hello"))) text += chunk.TextDelta;
        Assert.Equal("你好", text);
        Assert.IsType<IOException>(reported);
    }

    [Fact]
    public async Task SettingsImportDoesNotRewriteTheOriginalSettingsFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "yita-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var original = Path.Combine(directory, "original.json");
            var preview = Path.Combine(directory, "preview.json");
            await new JsonSettingsStore(original).SaveAsync(YitaSettings.Default with { TargetLanguage = "English", TargetLanguageMode = "fixed" });
            var originalBytes = await File.ReadAllBytesAsync(original);
            var store = new JsonSettingsStore(preview, original);
            Assert.Equal("英语", (await store.LoadAsync()).TargetLanguage);
            await store.SaveAsync(YitaSettings.Default with { TargetLanguage = "日本語", TargetLanguageMode = "fixed" });
            Assert.Equal("日语", (await store.LoadAsync()).TargetLanguage);
            Assert.Equal("英语", (await new JsonSettingsStore(original).LoadAsync()).TargetLanguage);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(original));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task HistoryCanBeClearedAndNewTranslationsCanBeWrittenAfterwards()
    {
        var directory = Path.Combine(Path.GetTempPath(), "yita-clear-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonlTranslationHistoryStore(Path.Combine(directory, "history.jsonl"));
            var entry = new TranslationHistoryEntry(DateTimeOffset.UtcNow, "hello", "你好", "en", "zh");
            await store.AppendAsync(entry);
            await store.ClearAsync();
            await store.ClearAsync();
            await store.AppendAsync(entry with { SourceText = "new" });
            var items = new List<TranslationHistoryEntry>();
            await foreach (var item in store.ReadAsync()) items.Add(item);
            Assert.Equal("new", Assert.Single(items).SourceText);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("https://example.com", "/v1/chat/completions")]
    [InlineData("https://example.com/v1/", "/v1/chat/completions")]
    [InlineData("https://example.com/chat/completions", "/chat/completions")]
    public async Task TranslatorUsesTargetLanguageAndAcceptsRoleAndUsageEvents(string endpoint, string path)
    {
        var handler = new StubHandler("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"bonjour\"}}]}\n\n"
            + "data: {\"choices\":[]}\n\ndata: [DONE]\n\n");
        using var client = new HttpClient(handler);
        var translator = new DeepSeekStreamingTranslator(client, new TranslationProviderOptions(new Uri(endpoint), "test", "test-key"));
        var chunks = new List<TranslationChunk>();
        await foreach (var chunk in translator.TranslateAsync(new TranslationRequest("hello", TargetLanguage: "French"))) chunks.Add(chunk);
        Assert.Equal(path, handler.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Contains("French", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("bonjour", chunks[0].TextDelta);
        Assert.True(chunks[^1].IsFinal);
    }

    [Theory]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n")]
    [InlineData("data: [DONE]\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":\"length\"}]}\n")]
    [InlineData("data: garbage\n")]
    public async Task InvalidOrTruncatedStreamFailsWithProtocolError(string sse)
    {
        using var client = new HttpClient(new StubHandler(sse));
        var translator = new DeepSeekStreamingTranslator(client, new TranslationProviderOptions(new Uri("https://example.com"), "test", "key"));
        var exception = await Assert.ThrowsAsync<TranslationProviderException>(async () =>
        {
            await foreach (var _ in translator.TranslateAsync(new TranslationRequest("hello"))) { }
        });
        Assert.Equal(TranslationFailureKind.Protocol, exception.Kind);
    }

    [Fact]
    public async Task CancelledCacheHitDoesNotReturnAnAnswer()
    {
        var cache = new MemoryTranslationCache();
        var request = new TranslationRequest("hello");
        cache.Set(request, "cached");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var coordinator = new TranslationCoordinator(new CompleteTranslator(), cache);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in coordinator.TranslateAsync(request, cancellation.Token)) { }
        });
    }

    private sealed class StubHandler(string sse) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse) };
        }
    }

    private sealed class PartialTranslator : IStreamingTranslator
    {
        public async IAsyncEnumerable<TranslationChunk> TranslateAsync(TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.Yield(); yield return new TranslationChunk("partial"); }
    }

    private sealed class CompleteTranslator : IStreamingTranslator
    {
        public async IAsyncEnumerable<TranslationChunk> TranslateAsync(TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.Yield(); yield return new TranslationChunk("你好", true); }
    }

    private sealed class FailingHistory : ITranslationHistory
    {
        public Task AppendAsync(TranslationHistoryEntry entry, CancellationToken cancellationToken = default) =>
            throw new IOException("Storage full.");
        public async IAsyncEnumerable<TranslationHistoryEntry> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.Yield(); yield break; }
    }
}
