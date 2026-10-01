using System.Text.Json;

namespace Yita.Core.Translation;

public sealed class MemoryTranslationCache : ITranslationCache
{
    private readonly Dictionary<TranslationRequest, string> _entries = new();
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Queue<TranslationRequest> _order = new();

    public MemoryTranslationCache(int capacity = 256)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public bool TryGet(TranslationRequest request, out string translation)
    {
        lock (_gate) return _entries.TryGetValue(Normalize(request), out translation!);
    }

    public bool TryGet(string sourceText, string sourceLanguage, string targetLanguage, out string translation) =>
        TryGet(new TranslationRequest(sourceText, sourceLanguage, targetLanguage), out translation);

    public void Set(string sourceText, string sourceLanguage, string targetLanguage, string translation)
    {
        Set(new TranslationRequest(sourceText, sourceLanguage, targetLanguage), translation);
    }

    public void Set(TranslationRequest request, string translation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(translation);
        var key = Normalize(request);
        lock (_gate)
        {
            if (!_entries.ContainsKey(key)) _order.Enqueue(key);
            _entries[key] = translation;
            while (_entries.Count > _capacity) _entries.Remove(_order.Dequeue());
        }
    }

    private static TranslationRequest Normalize(TranslationRequest request) => request with
    {
        Text = TextNormalizer.Normalize(request.Text),
        SourceLanguage = request.SourceLanguage.Trim(),
        TargetLanguage = request.TargetLanguage.Trim(),
    };
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) yield break;
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                TranslationHistoryEntry? entry;
                try { entry = JsonSerializer.Deserialize<TranslationHistoryEntry>(line, JsonOptions); }
                catch (JsonException) { continue; }
                if (entry is { SourceText: not null, TranslationText: not null }) yield return entry;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_path)) File.Delete(_path);
        }
        finally { _gate.Release(); }
    }
}
