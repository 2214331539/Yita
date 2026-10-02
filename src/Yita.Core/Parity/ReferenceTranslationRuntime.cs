using System.Runtime.CompilerServices;
using System.Text;
using Yita.Settings;
using Yita.Translation;
using Yita.Services;
using System.Diagnostics;

namespace Yita.Core.Parity;

// Presentation-independent orchestration of the original providers and policies.
internal sealed class ReferenceTranslationRuntime(ITranslationProviderFactory factory, TranslationMemoryStore? memory = null)
{
    private readonly TranslationMemoryCache _cache = new();
    private readonly TranslationInFlightRegistry _inFlight = new();
    private readonly SemaphoreSlim _translationSlots = new(2, 2);
    private readonly SemaphoreSlim _assistanceSlots = new(2, 2);

    internal TranslationRequest Prepare(string text, string? context, AppSettings settings)
    {
        var target = LanguageDirectionResolver.ResolveTargetLanguage(text, settings);
        return new TranslationRequest(text, settings.SourceLanguage, target,
            settings.UseSelectionContext ? context : null,
            TranslationPreferenceCatalog.NormalizeMode(settings.TranslationMode),
            TranslationPreferenceCatalog.NormalizeTone(settings.TranslationTone),
            ApplicableGlossaryEntries: PersonalGlossary.Parse(settings.PersonalGlossary)
                .Where(entry => text.Contains(entry.Source, StringComparison.OrdinalIgnoreCase)).ToArray(),
            TranslationExamples: memory?.FindRelevant(text, settings.SourceLanguage, target)
                .Select(entry => new TranslationExample(entry.SourceText, entry.TargetText)).ToArray() ?? []);
    }

    internal async IAsyncEnumerable<string> TranslateAsync(TranslationRequest request, AppSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default, TranslationPerformanceOperation? performance = null)
    {
        var saved = memory?.FindExact(request.Text, request.SourceLanguage, request.TargetLanguage);
        if (saved is not null && !TranslationOutputGuard.IsInstructionEcho(request.Text, saved.TargetText))
        { performance?.MarkCacheHit(); yield return saved.TargetText; yield break; }

        var signature = string.Join('\u001F', request.Context ?? "", request.Mode, request.Tone,
            string.Join('\n', (request.ApplicableGlossaryEntries ?? []).Select(entry => $"{entry.Source}=>{entry.Target}")),
            string.Join('\u001E', (request.TranslationExamples ?? []).Select(entry => $"{entry.SourceText}\u001D{entry.TargetText}")));
        var key = TranslationCacheKey.Create(settings.ProviderId, settings.DeepSeekEndpoint, settings.DeepSeekModel,
            request.SourceLanguage, request.TargetLanguage, request.Text, 8, signature);
        if (_cache.TryGet(key, out var cached) && !TranslationOutputGuard.IsInstructionEcho(request.Text, cached))
        { performance?.MarkCacheHit(); yield return cached; yield break; }

        while (_inFlight.TryJoin(key, out var existing) && existing is not null)
        {
            string? joined = null;
            try { joined = await existing.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { _inFlight.TryRemove(key, existing); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { _inFlight.TryRemove(key, existing); }
            if (joined is not null) { performance?.MarkCoalesced(); yield return joined; yield break; }
        }

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = completion.Task.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        if (!_inFlight.TryRegister(key, completion.Task))
        {
            await foreach (var output in TranslateAsync(request, settings, cancellationToken, performance).ConfigureAwait(false))
                yield return output;
            yield break;
        }
        var succeeded = false;
        var final = string.Empty;
        try
        {
            await foreach (var output in CollectAsync(token => factory.Create(settings).TranslateAsync(request, token),
                _translationSlots, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(20), cancellationToken, performance).ConfigureAwait(false))
            { final = output; yield return output; }
            if (string.IsNullOrWhiteSpace(final)) throw new TranslationProviderException("The service returned no translation.", TranslationFailureKind.Protocol);
            if (TranslationOutputGuard.IsInstructionEcho(request.Text, final))
                throw new TranslationProviderException("The service returned translation instructions. Please retry.", TranslationFailureKind.Protocol);
            _cache.Set(key, final);
            completion.TrySetResult(final);
            succeeded = true;
        }
        finally
        {
            if (!succeeded) completion.TrySetCanceled();
            _inFlight.TryRemove(key, completion.Task);
        }
    }

    internal IAsyncEnumerable<string> ExplainAsync(ExplanationRequest request, AppSettings settings, CancellationToken token) =>
        CollectAsync(cancellation => factory.CreateExplanationProvider(settings).ExplainAsync(request, cancellation),
            _assistanceSlots, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(15), token);

    internal IAsyncEnumerable<string> AnswerAsync(QuestionAnswerRequest request, AppSettings settings, CancellationToken token) =>
        CollectAsync(cancellation => factory.CreateQuestionAnswerProvider(settings).AnswerAsync(request, cancellation),
            _assistanceSlots, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(15), token);

    private static async IAsyncEnumerable<string> CollectAsync(Func<CancellationToken, IAsyncEnumerable<TranslationChunk>> create,
        SemaphoreSlim slots, TimeSpan totalTimeout, TimeSpan firstContentTimeout,
        [EnumeratorCancellation] CancellationToken cancellationToken, TranslationPerformanceOperation? performance = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(totalTimeout);
        performance?.MarkQueueStarted();
        try { await slots.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TranslationProviderException("Request timed out in the queue.", TranslationFailureKind.Timeout); }
        performance?.MarkQueueCompleted();
        var contentWaitStarted = Stopwatch.GetTimestamp();

        SafeAsyncEnumerator<TranslationChunk> enumerator;
        try { enumerator = new(create(timeout.Token).GetAsyncEnumerator(timeout.Token), () => slots.Release()); }
        catch { slots.Release(); throw; }
        await using var lease = enumerator;
        var output = new StringBuilder();
        var throttle = new StreamingUpdateThrottle();
        while (true)
        {
            bool hasNext;
            try
            {
                var waitBudget = output.Length == 0
                    ? firstContentTimeout - Stopwatch.GetElapsedTime(contentWaitStarted) : TimeSpan.FromSeconds(15);
                if (waitBudget <= TimeSpan.Zero) throw new TimeoutException();
                hasNext = await enumerator.MoveNextAsync().AsTask().WaitAsync(waitBudget, timeout.Token).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                timeout.Cancel();
                throw new TranslationProviderException("The streamed response timed out. Please retry.", exception, TranslationFailureKind.Timeout);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TranslationProviderException("Request timed out. Please retry.", TranslationFailureKind.Timeout); }
            if (!hasNext) break;
            if (output.Length == 0 && !string.IsNullOrEmpty(enumerator.Current.TextDelta)) performance?.MarkFirstContent();
            output.Append(enumerator.Current.TextDelta);
            if (throttle.ShouldPublish(output.Length, enumerator.Current.IsFinal))
                yield return HighlightMarkup.ToPlainText(output.ToString());
        }
        if (output.Length > 0) yield return HighlightMarkup.ToPlainText(output.ToString());
    }
}

internal sealed class InjectedTranslationProviderFactory(HttpClient client) : ITranslationProviderFactory
{
    private readonly ProviderCircuitBreaker _breaker = new();
    public IStreamingTranslationProvider Create(AppSettings settings) => settings.ProviderId == "mock"
        ? new MockTranslationProvider()
        : TranslationProviderFactory.TryValidateEndpoint(settings.DeepSeekEndpoint, out var endpoint)
            ? new CircuitBreakingTranslationProvider(new DeepSeekStreamingProvider(client,
                new OpenAiCompatibleProviderOptions(endpoint!, settings.DeepSeekModel, settings.DeepSeekApiKey)), _breaker)
            : throw new TranslationProviderException("Invalid endpoint.", TranslationFailureKind.Configuration);
    public IStreamingExplanationProvider CreateExplanationProvider(AppSettings settings) => (IStreamingExplanationProvider)Create(settings);
    public IStreamingQuestionAnswerProvider CreateQuestionAnswerProvider(AppSettings settings) => (IStreamingQuestionAnswerProvider)Create(settings);
    public IStreamingSummaryProvider CreateSummaryProvider(AppSettings settings) => (IStreamingSummaryProvider)Create(settings);
}
