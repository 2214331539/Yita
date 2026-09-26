using System.Collections.Concurrent;

namespace Yita.Translation;

/// <summary>
/// Lets later callers await an identical translation already in progress. The
/// owning caller still streams normally; joiners receive the final result and
/// avoid another provider request.
/// </summary>
internal sealed class TranslationInFlightRegistry
{
    private readonly ConcurrentDictionary<TranslationCacheKey, Task<string>> _requests = new();

    public int Count => _requests.Count;

    public bool TryJoin(TranslationCacheKey key, out Task<string>? completion) =>
        _requests.TryGetValue(key, out completion);

    public bool TryRegister(TranslationCacheKey key, Task<string> completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return _requests.TryAdd(key, completion);
    }

    public bool TryRemove(TranslationCacheKey key, Task<string> completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return _requests.TryRemove(new KeyValuePair<TranslationCacheKey, Task<string>>(key, completion));
    }
}
