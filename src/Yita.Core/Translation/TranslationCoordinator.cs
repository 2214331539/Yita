using System.Runtime.CompilerServices;
using System.Text;

namespace Yita.Core.Translation;

public sealed class TranslationCoordinator
{
    private readonly IStreamingTranslator _translator;
    private readonly ITranslationCache _cache;
    private readonly ITranslationHistory? _history;
    public event Action<Exception>? HistoryWriteFailed;

    public TranslationCoordinator(
        IStreamingTranslator translator,
        ITranslationCache? cache = null,
        ITranslationHistory? history = null)
    {
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
        _cache = cache ?? new MemoryTranslationCache();
        _history = history;
    }

    public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedText = TextNormalizer.Normalize(request.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedText);
        var normalized = request with { Text = normalizedText };

        if (_cache.TryGet(normalized, out var cached))
        {
            yield return new TranslationChunk(cached, true);
            yield break;
        }

        var fullText = new StringBuilder();
        var completed = false;
        await foreach (var chunk in _translator.TranslateAsync(normalized, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(chunk.TextDelta)) fullText.Append(chunk.TextDelta);
            completed |= chunk.IsFinal;
            yield return chunk;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!completed)
            throw new TranslationProviderException("翻译响应提前结束，请重试。", TranslationFailureKind.Protocol);
        if (fullText.Length == 0) yield break;
        var translation = fullText.ToString().Trim();
        if (translation.Length == 0) yield break;
        _cache.Set(normalized, translation);
        if (_history is not null)
        {
            try
            {
                await _history.AppendAsync(new TranslationHistoryEntry(
                    DateTimeOffset.UtcNow,
                    normalized.Text,
                    translation,
                    normalized.SourceLanguage,
                    normalized.TargetLanguage), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                HistoryWriteFailed?.Invoke(exception);
            }
        }
    }
}

public static class TextNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }
}
