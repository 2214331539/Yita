using System.Runtime.CompilerServices;
using Yita.Core.Parity;
using Yita.Settings;
using Yita.Translation;

namespace Yita.Core.Parity.Tests;

public sealed class ReferenceTranslationRuntimeTests
{
    [Fact]
    public async Task DuplicateSelectionsShareOneRequestAndCompletedResultsAreCached()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new Factory { BeforeTranslation = async _ => { started.SetResult(); await release.Task; } };
        var runtime = new ReferenceTranslationRuntime(factory);
        var request = new TranslationRequest("source", "英语", "简体中文");
        var first = Final(runtime.TranslateAsync(request, AppSettings.Default));
        await started.Task;
        var joined = Final(runtime.TranslateAsync(request, AppSettings.Default));
        release.SetResult();
        Assert.Equal("译文", await first);
        Assert.Equal("译文", await joined);
        Assert.Equal("译文", await Final(runtime.TranslateAsync(request, AppSettings.Default)));
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public async Task CancellingAJoinedSelectionDoesNotCancelTheOriginal()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new Factory { BeforeTranslation = async _ => { started.SetResult(); await release.Task; } };
        var runtime = new ReferenceTranslationRuntime(factory);
        var request = new TranslationRequest("source", "英语", "简体中文");
        var original = Final(runtime.TranslateAsync(request, AppSettings.Default));
        await started.Task;
        using var cancellation = new CancellationTokenSource();
        var joined = Final(runtime.TranslateAsync(request, AppSettings.Default, cancellation.Token));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joined);
        release.SetResult();
        Assert.Equal("译文", await original);
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public async Task ContextToneGlossaryAndExamplesParticipateInCacheIdentity()
    {
        var factory = new Factory();
        var runtime = new ReferenceTranslationRuntime(factory);
        var request = new TranslationRequest("source", "英语", "简体中文");
        await Final(runtime.TranslateAsync(request, AppSettings.Default));
        await Final(runtime.TranslateAsync(request with { Context = "context" }, AppSettings.Default));
        await Final(runtime.TranslateAsync(request with { Tone = "formal" }, AppSettings.Default));
        await Final(runtime.TranslateAsync(request with { ApplicableGlossaryEntries = [new GlossaryEntry("source", "术语")] }, AppSettings.Default));
        await Final(runtime.TranslateAsync(request with { TranslationExamples = [new TranslationExample("sample", "修正")] }, AppSettings.Default));
        Assert.Equal(5, factory.Calls);
    }

    [Fact]
    public async Task AFailedTranslationIsNotCachedAndCanBeRetried()
    {
        var factory = new Factory { BeforeTranslation = _ => throw new TranslationProviderException("failed", TranslationFailureKind.Server) };
        var runtime = new ReferenceTranslationRuntime(factory);
        var request = new TranslationRequest("source", "英语", "简体中文");
        await Assert.ThrowsAsync<TranslationProviderException>(() => Final(runtime.TranslateAsync(request, AppSettings.Default)));
        factory.BeforeTranslation = _ => Task.CompletedTask;
        Assert.Equal("译文", await Final(runtime.TranslateAsync(request, AppSettings.Default)));
        Assert.Equal(2, factory.Calls);
    }

    [Fact]
    public async Task ExplainAndAnswerUseOriginalRequestsAndStripHighlightMarkup()
    {
        var factory = new Factory { Output = "[[h1:重点]]内容" };
        var runtime = new ReferenceTranslationRuntime(factory);
        var explanation = new ExplanationRequest("subject", "source", "translated", "英语", "简体中文", ExplanationScope.CodeAnalysis);
        var question = new QuestionAnswerRequest("question", "source", "translated", "explained", "英语", "简体中文", "zh-CN", QuestionContextKind.Explanation,
            [new ConversationTurn("user", "previous"), new ConversationTurn("assistant", "answer")]);
        Assert.Equal("重点内容", await Final(runtime.ExplainAsync(explanation, AppSettings.Default, default)));
        Assert.Equal("重点内容", await Final(runtime.AnswerAsync(question, AppSettings.Default, default)));
        Assert.Same(explanation, factory.Explanation);
        Assert.Same(question, factory.Question);
    }

    [Fact]
    public async Task SavedCorrectionsOverrideCachedTranslationsWithoutCallingTheProvider()
    {
        var directory = Path.Combine(Path.GetTempPath(), "yita-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            var memory = new TranslationMemoryStore(Path.Combine(directory, "memory.dat"), new TestProtector());
            var factory = new Factory();
            var runtime = new ReferenceTranslationRuntime(factory, memory);
            var request = new TranslationRequest("source", "英语", "简体中文");
            await Final(runtime.TranslateAsync(request, AppSettings.Default));
            memory.AddOrUpdate("source", "用户修正", "英语", "简体中文");
            Assert.Equal("用户修正", await Final(runtime.TranslateAsync(request, AppSettings.Default)));
            Assert.Equal(1, factory.Calls);
            Assert.Equal("用户修正", new TranslationMemoryStore(Path.Combine(directory, "memory.dat"), new TestProtector()).FindExact("source", "英语", "简体中文")?.TargetText);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task<string> Final(IAsyncEnumerable<string> stream)
    {
        var result = "";
        await foreach (var text in stream) result = text;
        return result;
    }
    private sealed class TestProtector : ITranslationMemoryProtector
    {
        public byte[] Protect(byte[] bytes) => bytes.Select(value => (byte)(value ^ 0x6A)).ToArray();
        public byte[] Unprotect(byte[] bytes) => Protect(bytes);
    }
    private sealed class Factory : ITranslationProviderFactory, IStreamingTranslationProvider, IStreamingExplanationProvider, IStreamingQuestionAnswerProvider, IStreamingSummaryProvider
    {
        internal Func<CancellationToken, Task> BeforeTranslation { get; set; } = _ => Task.CompletedTask;
        internal int Calls;
        internal string Output = "译文";
        internal ExplanationRequest? Explanation;
        internal QuestionAnswerRequest? Question;
        public string Id => "test";
        public IStreamingTranslationProvider Create(AppSettings settings) => this;
        public IStreamingExplanationProvider CreateExplanationProvider(AppSettings settings) => this;
        public IStreamingQuestionAnswerProvider CreateQuestionAnswerProvider(AppSettings settings) => this;
        public IStreamingSummaryProvider CreateSummaryProvider(AppSettings settings) => this;
        public async IAsyncEnumerable<TranslationChunk> TranslateAsync(TranslationRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { Interlocked.Increment(ref Calls); await BeforeTranslation(token); token.ThrowIfCancellationRequested(); yield return new TranslationChunk(Output, true); }
        public IAsyncEnumerable<TranslationChunk> ExplainAsync(ExplanationRequest request, CancellationToken token = default)
        { Explanation = request; return TranslateAsync(new TranslationRequest("", "", ""), token); }
        public IAsyncEnumerable<TranslationChunk> AnswerAsync(QuestionAnswerRequest request, CancellationToken token = default)
        { Question = request; return TranslateAsync(new TranslationRequest("", "", ""), token); }
        public IAsyncEnumerable<TranslationChunk> SummarizeAsync(SummaryRequest request, CancellationToken token = default) => TranslateAsync(new TranslationRequest("", "", ""), token);
    }
}
