namespace Yita.Translation;

internal sealed class ProviderCircuitBreaker
{
    internal const int DefaultFailureThreshold = 3;
    internal static readonly TimeSpan DefaultBreakDuration = TimeSpan.FromSeconds(6);

    private readonly object _syncRoot = new();
    private readonly int _failureThreshold;
    private readonly TimeSpan _breakDuration;
    private readonly Func<DateTimeOffset> _utcNow;
    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;

    public ProviderCircuitBreaker(
        int failureThreshold = DefaultFailureThreshold,
        TimeSpan? breakDuration = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        if (failureThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(failureThreshold));
        }

        var effectiveBreakDuration = breakDuration ?? DefaultBreakDuration;
        if (effectiveBreakDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(breakDuration));
        }

        _failureThreshold = failureThreshold;
        _breakDuration = effectiveBreakDuration;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public void ThrowIfOpen()
    {
        lock (_syncRoot)
        {
            if (_openUntil is not { } openUntil)
            {
                return;
            }

            var now = _utcNow();
            if (now >= openUntil)
            {
                _openUntil = null;
                _consecutiveFailures = 0;
                return;
            }

            var remainingSeconds = Math.Max(1, (int)Math.Ceiling((openUntil - now).TotalSeconds));
            throw new TranslationProviderException(
                $"DeepSeek 因连续网络故障暂时暂停，请在 {remainingSeconds} 秒后重试。",
                TranslationFailureKind.Connectivity);
        }
    }

    public void RecordSuccess()
    {
        lock (_syncRoot)
        {
            _consecutiveFailures = 0;
            _openUntil = null;
        }
    }

    public void RecordFailure(TranslationProviderException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_syncRoot)
        {
            if (!exception.IsTransient)
            {
                _consecutiveFailures = 0;
                _openUntil = null;
                return;
            }

            _consecutiveFailures++;
            if (_consecutiveFailures >= _failureThreshold)
            {
                _openUntil = _utcNow() + _breakDuration;
            }
        }
    }
}

internal sealed class CircuitBreakingTranslationProvider(
    IStreamingTranslationProvider inner,
    ProviderCircuitBreaker circuitBreaker) :
    IStreamingTranslationProvider,
    IStreamingExplanationProvider,
    IStreamingQuestionAnswerProvider,
    IStreamingSummaryProvider
{
    public string Id => inner.Id;

    public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
        TranslationRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in StreamWithCircuitBreakerAsync(
                           inner.TranslateAsync(request, cancellationToken),
                           cancellationToken))
        {
            yield return chunk;
        }
    }

    public async IAsyncEnumerable<TranslationChunk> ExplainAsync(
        ExplanationRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (inner is not IStreamingExplanationProvider explanationProvider)
        {
            throw new TranslationProviderException(
                "当前 Provider 不支持 AI 解释。",
                TranslationFailureKind.Configuration);
        }

        await foreach (var chunk in StreamWithCircuitBreakerAsync(
                           explanationProvider.ExplainAsync(request, cancellationToken),
                           cancellationToken))
        {
            yield return chunk;
        }
    }

    public async IAsyncEnumerable<TranslationChunk> AnswerAsync(
        QuestionAnswerRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (inner is not IStreamingQuestionAnswerProvider questionAnswerProvider)
        {
            throw new TranslationProviderException(
                "当前 Provider 不支持 AI 问答。",
                TranslationFailureKind.Configuration);
        }

        await foreach (var chunk in StreamWithCircuitBreakerAsync(
                           questionAnswerProvider.AnswerAsync(request, cancellationToken),
                           cancellationToken))
        {
            yield return chunk;
        }
    }

    public async IAsyncEnumerable<TranslationChunk> SummarizeAsync(
        SummaryRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (inner is not IStreamingSummaryProvider summaryProvider)
        {
            throw new TranslationProviderException(
                "当前 Provider 不支持 AI 总结。",
                TranslationFailureKind.Configuration);
        }

        await foreach (var chunk in StreamWithCircuitBreakerAsync(
                           summaryProvider.SummarizeAsync(request, cancellationToken),
                           cancellationToken))
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<TranslationChunk> StreamWithCircuitBreakerAsync(
        IAsyncEnumerable<TranslationChunk> stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        circuitBreaker.ThrowIfOpen();
        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (TranslationProviderException exception)
            {
                circuitBreaker.RecordFailure(exception);
                throw;
            }

            if (!hasNext)
            {
                circuitBreaker.RecordSuccess();
                yield break;
            }

            yield return enumerator.Current;
        }
    }
}
