namespace Yita.Core.Translation;

public sealed record TranslationRequest(
    string Text,
    string SourceLanguage = "自动检测",
    string TargetLanguage = "简体中文",
    string? Context = null,
    string Mode = "natural",
    string Tone = "neutral",
    string PersonalGlossary = "");

public sealed record TranslationChunk(string TextDelta, bool IsFinal = false);

public sealed record TranslationProviderOptions(Uri Endpoint, string Model, string ApiKey);

public enum TranslationFailureKind
{
    Unknown,
    Configuration,
    Authentication,
    InvalidRequest,
    Connectivity,
    Timeout,
    RateLimit,
    Server,
    Protocol,
    Cancelled,
}

public sealed class TranslationProviderException : Exception
{
    public TranslationFailureKind Kind { get; }

    public bool IsTransient => Kind is TranslationFailureKind.Connectivity
        or TranslationFailureKind.Timeout
        or TranslationFailureKind.RateLimit
        or TranslationFailureKind.Server;

    public TranslationProviderException(string message, TranslationFailureKind kind = TranslationFailureKind.Unknown)
        : base(message) => Kind = kind;

    public TranslationProviderException(string message, Exception inner, TranslationFailureKind kind)
        : base(message, inner) => Kind = kind;
}

public interface IStreamingTranslator
{
    IAsyncEnumerable<TranslationChunk> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);
}

public interface ITranslationCache
{
    bool TryGet(TranslationRequest request, out string translation);

    void Set(TranslationRequest request, string translation);
}

public interface ITranslationHistory
{
    Task AppendAsync(TranslationHistoryEntry entry, CancellationToken cancellationToken = default);

    IAsyncEnumerable<TranslationHistoryEntry> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed record TranslationHistoryEntry(
    DateTimeOffset CreatedAt,
    string SourceText,
    string TranslationText,
    string SourceLanguage,
    string TargetLanguage);
