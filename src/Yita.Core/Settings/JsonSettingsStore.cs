using System.Text.Json;
using System.Text.Json.Serialization;
using Yita.Settings;

namespace Yita.Core.Settings;

public enum SettingsFailureKind { InvalidContent, NewerVersion, Unavailable }

public sealed class SettingsStoreException(SettingsFailureKind kind, Exception? inner = null)
    : IOException(kind switch
    {
        SettingsFailureKind.NewerVersion => "Settings were saved by a newer Yita version. Update Yita to edit them.",
        SettingsFailureKind.InvalidContent => "Settings could not be read. The existing file has been preserved.",
        _ => "Settings are unavailable or changed during saving. The existing file has been preserved.",
    }, inner)
{
    public SettingsFailureKind Kind { get; } = kind;
}

public sealed class JsonSettingsStore : ISettingsStore
{
    private const int MaximumFileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions OriginalOptions = new(JsonOptions)
        { Converters = { new JsonStringEnumConverter() } };
    private readonly string _path;
    private readonly string? _fallbackPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore(string? path = null, string? fallbackPath = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yita", "settings.json"));
        _fallbackPath = fallbackPath is null ? null : Path.GetFullPath(fallbackPath);
    }

    public async Task<YitaSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadAsync(_path, cancellationToken).ConfigureAwait(false);
            if (document is not null) return Normalize(Deserialize<YitaSettings>(document.Root, JsonOptions));
            if (_fallbackPath is null) return YitaSettings.Default;
            document = await ReadAsync(_fallbackPath, cancellationToken).ConfigureAwait(false);
            if (document is null) return YitaSettings.Default;
            // Import preferences without activating legacy startup or sharing its record destination.
            var original = Deserialize<AppSettings>(document.Root, OriginalOptions);
            return Normalize(YitaSettings.FromOriginal(original)) with
            {
                AiHistoryEnabled = false, AiHistoryDirectory = string.Empty, StartWithSystem = false,
                UseClipboardFallback = true,
            };
        }
        finally { _gate.Release(); }
    }

    public async Task ValidateWriteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await ReadAsync(_path, cancellationToken).ConfigureAwait(false);
            if (existing is not null) _ = Normalize(Deserialize<YitaSettings>(existing.Root, JsonOptions));
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(YitaSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SchemaVersion > YitaSettings.CurrentSchemaVersion)
            throw new SettingsStoreException(SettingsFailureKind.NewerVersion);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var existing = await ReadAsync(_path, cancellationToken).ConfigureAwait(false);
            if (existing is not null) _ = Normalize(Deserialize<YitaSettings>(existing.Root, JsonOptions));
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream,
                    settings with { SchemaVersion = YitaSettings.CurrentSchemaVersion }, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                if (stream.Length > MaximumFileBytes) throw new SettingsStoreException(SettingsFailureKind.InvalidContent);
            }
            // Recheck before replacing so a future/corrupt version cannot be overwritten after loading.
            var current = await ReadAsync(_path, cancellationToken).ConfigureAwait(false);
            if ((existing is null) != (current is null)
                || existing is not null && !existing.Bytes.AsSpan().SequenceEqual(current!.Bytes))
                throw new SettingsStoreException(SettingsFailureKind.Unavailable);
            if (existing is { Version: 0 })
                await BackupAsync(existing.Bytes, temporaryPath + ".backup", cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            DeleteTemporary(temporaryPath);
            DeleteTemporary(temporaryPath + ".backup");
            _gate.Release();
        }
    }

    private async Task BackupAsync(byte[] bytes, string temporaryPath, CancellationToken cancellationToken)
    {
        var backupPath = _path + ".schema-0.bak";
        if (File.Exists(backupPath)) return;
        await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        try { File.Move(temporaryPath, backupPath); }
        catch (IOException) when (File.Exists(backupPath)) { }
    }

    private static YitaSettings Normalize(YitaSettings settings) => YitaSettings.FromOriginal(settings.ToOriginal()) with
    {
        SchemaVersion = YitaSettings.CurrentSchemaVersion,
        PopupOffsetX = settings.PopupOffsetX, PopupOffsetY = settings.PopupOffsetY,
        UseClipboardFallback = settings.SelectionCompatibilityVersion < 1 || settings.UseClipboardFallback,
        SelectionCompatibilityVersion = 1,
    };

    private static T Deserialize<T>(JsonElement root, JsonSerializerOptions options)
    {
        try { return root.Deserialize<T>(options) ?? throw new SettingsStoreException(SettingsFailureKind.InvalidContent); }
        catch (JsonException exception) { throw new SettingsStoreException(SettingsFailureKind.InvalidContent, exception); }
    }

    private static async Task<SettingsDocument?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaximumFileBytes) throw new SettingsStoreException(SettingsFailureKind.InvalidContent);
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new SettingsStoreException(SettingsFailureKind.InvalidContent);
            var version = 0;
            var foundVersion = false;
            foreach (var property in root.EnumerateObject())
            {
                if (!property.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase)) continue;
                if (foundVersion || property.Value.ValueKind != JsonValueKind.Number
                    || !property.Value.TryGetInt32(out version) || version < 0)
                    throw new SettingsStoreException(SettingsFailureKind.InvalidContent);
                foundVersion = true;
            }
            if (version > YitaSettings.CurrentSchemaVersion) throw new SettingsStoreException(SettingsFailureKind.NewerVersion);
            return new SettingsDocument(bytes, root.Clone(), version);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException exception) { throw new SettingsStoreException(SettingsFailureKind.InvalidContent, exception); }
        catch (Exception exception) when (exception is UnauthorizedAccessException || exception is IOException and not SettingsStoreException)
        { throw new SettingsStoreException(SettingsFailureKind.Unavailable, exception); }
    }

    private static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private sealed record SettingsDocument(byte[] Bytes, JsonElement Root, int Version);
}
