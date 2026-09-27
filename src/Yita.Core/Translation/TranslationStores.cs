using System.Collections.Concurrent;
using System.Text.Json;

namespace Yita.Core.Translation;

public sealed class MemoryTranslationCache : ITranslationCache
{
    private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly ConcurrentQueue<string> _order = new();

    public MemoryTranslationCache(int capacity = 256)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public bool TryGet(string sourceText, string sourceLanguage, string targetLanguage, out string translation) =>
        _entries.TryGetValue(Key(sourceText, sourceLanguage, targetLanguage), out translation!);

    public void Set(string sourceText, string sourceLanguage, string targetLanguage, string translation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(translation);
        var key = Key(sourceText, sourceLanguage, targetLanguage);
        _entries[key] = translation;
        _order.Enqueue(key);
        while (_entries.Count > _capacity && _order.TryDequeue(out var oldest))
            _entries.TryRemove(oldest, out _);
    }

    private static string Key(string sourceText, string sourceLanguage, string targetLanguage) =>
        string.Join('\u001f', sourceLanguage.Trim(), targetLanguage.Trim(), sourceText.Trim());
}

public sealed class JsonlTranslationHistoryStore : ITranslationHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonlTranslationHistoryStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public async Task AppendAsync(TranslationHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(stream, entry, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<TranslationHistoryEntry> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) yield break;
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            TranslationHistoryEntry? entry;
            try { entry = JsonSerializer.Deserialize<TranslationHistoryEntry>(line, JsonOptions); }
            catch (JsonException) { continue; }
            if (entry is not null) yield return entry;
        }
    }
}
