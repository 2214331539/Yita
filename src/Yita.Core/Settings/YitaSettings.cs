using System.Text.Json;

namespace Yita.Core.Settings;

public sealed record YitaSettings
{
    public bool IsEnabled { get; init; } = true;
    public bool StartWithSystem { get; init; }
    public bool UseClipboardFallback { get; init; }
    public bool UseSelectionContext { get; init; }
    public int SelectionDelayMilliseconds { get; init; } = 80;
    public int MaximumSelectionCharacters { get; init; } = 8000;
    public string SourceLanguage { get; init; } = "自动检测";
    public string TargetLanguage { get; init; } = "简体中文";
    public string TranslationMode { get; init; } = "natural";
    public string TranslationTone { get; init; } = "neutral";
    public string ColorTheme { get; init; } = "yita-lakeside";
    public bool AiHistoryEnabled { get; init; }
    public string ProviderId { get; init; } = "deepseek";
    public string DeepSeekEndpoint { get; init; } = "https://api.deepseek.com";
    public string DeepSeekModel { get; init; } = "deepseek-v4-flash";
    public static YitaSettings Default { get; } = new();
}

public interface ISettingsStore
{
    Task<YitaSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(YitaSettings settings, CancellationToken cancellationToken = default);
}

public interface ISecretStore
{
    Task<string?> ReadApiKeyAsync(CancellationToken cancellationToken = default);

    Task SaveApiKeyAsync(string value, CancellationToken cancellationToken = default);
}

public sealed class MemorySecretStore : ISecretStore
{
    private string? _apiKey;

    public Task<string?> ReadApiKeyAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_apiKey);

    public Task SaveApiKeyAsync(string value, CancellationToken cancellationToken = default)
    {
        _apiKey = value?.Trim();
        return Task.CompletedTask;
    }
}

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    public JsonSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yita", "settings.json");
    }

    public async Task<YitaSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_path)) return YitaSettings.Default;
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<YitaSettings>(stream, JsonOptions, cancellationToken)
                ?? YitaSettings.Default;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return YitaSettings.Default;
        }
    }

    public async Task SaveAsync(YitaSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch (IOException) { }
        }
    }
}
