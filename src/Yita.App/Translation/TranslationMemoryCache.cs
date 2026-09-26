using System.Security.Cryptography;
using System.Text;

namespace Yita.Translation;

/// <summary>
/// Identifies a completed translation. Keep the prompt version in the key so a
/// prompt change cannot silently reuse output produced by older instructions.
/// </summary>
internal readonly record struct TranslationCacheKey(
    string ProviderId,
    string Endpoint,
    string Model,
    string SourceLanguage,
    string TargetLanguage,
    string TextFingerprint,
    int PromptVersion,
    string OptionsFingerprint)
{
    public static TranslationCacheKey Create(
        string providerId,
        string endpoint,
        string model,
        string sourceLanguage,
        string targetLanguage,
        string text,
        int promptVersion,
        string? optionsSignature = null)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(sourceLanguage);
        ArgumentNullException.ThrowIfNull(targetLanguage);
        ArgumentNullException.ThrowIfNull(text);

        return new TranslationCacheKey(
            providerId.Trim(),
            endpoint.Trim().TrimEnd('/'),
            model.Trim(),
            sourceLanguage.Trim(),
            targetLanguage.Trim(),
            CreateFingerprint(text),
            promptVersion,
            CreateFingerprint(optionsSignature));
    }

    private static string CreateFingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

/// <summary>
/// A small process-local LRU cache for complete translations. Partial streaming
/// output must never be inserted by callers.
/// </summary>
internal sealed class TranslationMemoryCache
{
    internal const int DefaultMaxEntries = 128;
    internal const int DefaultMaxCharacterCount = 1024 * 1024;
    internal static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromMinutes(20);

    private readonly object _syncRoot = new();
    private readonly int _maxEntries;
    private readonly int _maxCharacterCount;
    private readonly TimeSpan _timeToLive;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<TranslationCacheKey, LinkedListNode<CacheEntry>> _entries = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private int _characterCount;

    public TranslationMemoryCache(
        int maxEntries = DefaultMaxEntries,
        int maxCharacterCount = DefaultMaxCharacterCount,
        TimeSpan? timeToLive = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        }

        if (maxCharacterCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCharacterCount));
        }

        var effectiveTimeToLive = timeToLive ?? DefaultTimeToLive;
        if (effectiveTimeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeToLive));
        }

        _maxEntries = maxEntries;
        _maxCharacterCount = maxCharacterCount;
        _timeToLive = effectiveTimeToLive;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public int Count
    {
        get
        {
            lock (_syncRoot)
            {
                RemoveExpiredEntries(_utcNow());
                return _entries.Count;
            }
        }
    }

    public int CharacterCount
    {
        get
        {
            lock (_syncRoot)
            {
                RemoveExpiredEntries(_utcNow());
                return _characterCount;
            }
        }
    }

    public bool TryGet(TranslationCacheKey key, out string translation)
    {
        lock (_syncRoot)
        {
            var now = _utcNow();
            RemoveExpiredEntries(now);
            if (!_entries.TryGetValue(key, out var node))
            {
                translation = string.Empty;
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            translation = node.Value.Translation;
            return true;
        }
    }

    public void Set(TranslationCacheKey key, string translation)
    {
        ArgumentNullException.ThrowIfNull(translation);

        lock (_syncRoot)
        {
            var now = _utcNow();
            RemoveExpiredEntries(now);

            if (_entries.TryGetValue(key, out var existing))
            {
                RemoveNode(existing);
            }

            var calculatedCharacterCost = CalculateCharacterCost(key, translation);
            if (calculatedCharacterCost > _maxCharacterCount)
            {
                return;
            }

            var characterCost = (int)calculatedCharacterCost;

            var entry = new CacheEntry(
                key,
                translation,
                now + _timeToLive,
                characterCost);
            var node = _lru.AddFirst(entry);
            _entries.Add(key, node);
            _characterCount += characterCost;

            TrimToLimits();
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _entries.Clear();
            _lru.Clear();
            _characterCount = 0;
        }
    }

    private static long CalculateCharacterCost(TranslationCacheKey key, string translation)
    {
        return (long)key.ProviderId.Length
            + key.Endpoint.Length
            + key.Model.Length
            + key.SourceLanguage.Length
            + key.TargetLanguage.Length
            + key.TextFingerprint.Length
            + key.OptionsFingerprint.Length
            + translation.Length;
    }

    private void RemoveExpiredEntries(DateTimeOffset now)
    {
        var node = _lru.Last;
        while (node is not null)
        {
            var previous = node.Previous;
            if (node.Value.ExpiresAt <= now)
            {
                RemoveNode(node);
            }

            node = previous;
        }
    }

    private void TrimToLimits()
    {
        while (_entries.Count > _maxEntries || _characterCount > _maxCharacterCount)
        {
            var leastRecentlyUsed = _lru.Last;
            if (leastRecentlyUsed is null)
            {
                break;
            }

            RemoveNode(leastRecentlyUsed);
        }
    }

    private void RemoveNode(LinkedListNode<CacheEntry> node)
    {
        _lru.Remove(node);
        _entries.Remove(node.Value.Key);
        _characterCount -= node.Value.CharacterCost;
    }

    private sealed record CacheEntry(
        TranslationCacheKey Key,
        string Translation,
        DateTimeOffset ExpiresAt,
        int CharacterCost);
}
