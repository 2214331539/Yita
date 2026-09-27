using System.Runtime.CompilerServices;
using System.Text;

namespace Yita.Core.Translation;

public sealed class TranslationCoordinator
{
    private readonly IStreamingTranslator _translator;
    private readonly ITranslationCache _cache;
    private readonly ITranslationHistory? _history;

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
        var normalizedText = TextNormalizer.Normalize(request.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedText);
        var normalized = request with { Text = normalizedText };

        if (_cache.TryGet(normalized.Text, normalized.SourceLanguage, normalized.TargetLanguage, out var cached))
        {
            yield return new TranslationChunk(cached, true);
            yield break;
        }

        var fullText = new StringBuilder();
        await foreach (var chunk in _translator.TranslateAsync(normalized, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(chunk.TextDelta)) fullText.Append(chunk.TextDelta);
            yield return chunk;
        }

        if (fullText.Length == 0) yield break;
        var translation = fullText.ToString().Trim();
        _cache.Set(normalized.Text, normalized.SourceLanguage, normalized.TargetLanguage, translation);
        if (_history is not null)
        {
            await _history.AppendAsync(new TranslationHistoryEntry(
                DateTimeOffset.UtcNow,
                normalized.Text,
                translation,
                normalized.SourceLanguage,
                normalized.TargetLanguage), cancellationToken).ConfigureAwait(false);
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
