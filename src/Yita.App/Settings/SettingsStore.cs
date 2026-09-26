using System.IO;
using System.Text.Json;

namespace Yita.Settings;

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IApiKeyStore _credentialStore;
    private readonly string _settingsPath;
    private int _apiKeyReadFailed;

    public SettingsStore(IApiKeyStore credentialStore, string? settingsPath = null)
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Yita",
            "settings.json");
    }

    public bool ApiKeyReadFailed => Volatile.Read(ref _apiKeyReadFailed) != 0;

    public bool SettingsReadFailed { get; private set; }

    public AppSettings Load()
    {
        var settings = LoadPreferences();
        return TryReadApiKey(out var apiKey)
            ? settings with { DeepSeekApiKey = apiKey }
            : settings with { DeepSeekApiKey = string.Empty };
    }

    public AppSettings LoadPreferences()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(_settingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions)
                    ?? AppSettings.Default
                : AppSettings.Default;
            SettingsReadFailed = false;
        }
        catch (JsonException)
        {
            settings = AppSettings.Default;
            SettingsReadFailed = true;
        }
        catch (IOException)
        {
            settings = AppSettings.Default;
            SettingsReadFailed = true;
        }
        catch (UnauthorizedAccessException)
        {
            settings = AppSettings.Default;
            SettingsReadFailed = true;
        }

        return NormalizeSettings(settings) with { DeepSeekApiKey = string.Empty };
    }

    public bool TryReadApiKey(out string apiKey)
    {
        try
        {
            apiKey = _credentialStore.ReadApiKey();
            Volatile.Write(ref _apiKeyReadFailed, 0);
            return true;
        }
        catch
        {
            apiKey = string.Empty;
            Volatile.Write(ref _apiKeyReadFailed, 1);
            return false;
        }
    }

    public void Save(AppSettings settings, bool persistApiKey = true)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("设置文件路径无效。");
        Directory.CreateDirectory(directory);

        var normalizedSettings = NormalizeSettings(settings);
        var temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(
                normalizedSettings with { DeepSeekApiKey = string.Empty },
                JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (persistApiKey)
        {
            _credentialStore.SaveApiKey(settings.DeepSeekApiKey);
            Volatile.Write(ref _apiKeyReadFailed, 0);
        }
    }

    internal static AppSettings NormalizeSettings(AppSettings? settings)
    {
        var value = settings ?? AppSettings.Default;
        var defaults = AppSettings.Default;
        if (value.BrandPaletteVersion < 1)
        {
            value = value with
            {
                ColorTheme = string.Equals(value.ColorTheme, "ocean", StringComparison.OrdinalIgnoreCase)
                    ? ThemeCatalog.DefaultThemeId : value.ColorTheme,
                HighlightPalette = string.Equals(value.HighlightPalette, "clarity", StringComparison.OrdinalIgnoreCase)
                    ? HighlightPaletteCatalog.DefaultPaletteId : value.HighlightPalette,
            };
        }
        // Upgrade only the old default pair. Preserve individually chosen reading fonts.
        if (value.TypographyVersion < 1
            && TranslationFontCatalog.NormalizeEnglish(value.EnglishTranslationFontFamily) == "Times New Roman"
            && TranslationFontCatalog.NormalizeChinese(value.ChineseTranslationFontFamily) == "SimHei")
        {
            value = value with
            {
                EnglishTranslationFontFamily = TranslationFontCatalog.DefaultEnglishFontFamily,
                ChineseTranslationFontFamily = TranslationFontCatalog.DefaultChineseFontFamily,
            };
        }
        return value with
        {
            TypographyVersion = 1,
            BrandPaletteVersion = 1,
            UiLanguage = UiLanguageCatalog.Normalize(value.UiLanguage),
            ProviderId = string.Equals(value.ProviderId, "mock", StringComparison.OrdinalIgnoreCase)
                ? "mock"
                : "deepseek",
            DeepSeekEndpoint = string.IsNullOrWhiteSpace(value.DeepSeekEndpoint)
                ? defaults.DeepSeekEndpoint
                : value.DeepSeekEndpoint.Trim(),
            DeepSeekModel = string.IsNullOrWhiteSpace(value.DeepSeekModel)
                ? defaults.DeepSeekModel
                : value.DeepSeekModel.Trim(),
            SourceLanguage = string.IsNullOrWhiteSpace(value.SourceLanguage)
                ? defaults.SourceLanguage
                : value.SourceLanguage.Trim(),
            TargetLanguage = string.IsNullOrWhiteSpace(value.TargetLanguage)
                ? defaults.TargetLanguage
                : value.TargetLanguage.Trim(),
            TargetLanguageMode = string.Equals(value.TargetLanguageMode, "fixed", StringComparison.OrdinalIgnoreCase)
                ? "fixed"
                : "auto",
            TranslationMode = TranslationPreferenceCatalog.NormalizeMode(value.TranslationMode),
            TranslationTone = TranslationPreferenceCatalog.NormalizeTone(value.TranslationTone),
            PersonalGlossary = value.PersonalGlossary?.Trim() ?? string.Empty,
            ColorTheme = string.IsNullOrWhiteSpace(value.ColorTheme)
                ? defaults.ColorTheme
                : value.ColorTheme.Trim(),
            CustomAccentColor = string.IsNullOrWhiteSpace(value.CustomAccentColor)
                ? defaults.CustomAccentColor
                : value.CustomAccentColor.Trim(),
            PopupVisualStyle = PopupVisualStyleCatalog.Normalize(value.PopupVisualStyle),
            HighlightPalette = HighlightPaletteCatalog.Normalize(value.HighlightPalette),
            AiHistoryDirectory = value.AiHistoryDirectory?.Trim() ?? string.Empty,
            AiSummaryRange = SummaryRangeCatalog.Normalize(value.AiSummaryRange),
            SelectionDelayMilliseconds = Math.Clamp(value.SelectionDelayMilliseconds, 0, 2000),
            MaximumSelectionCharacters = Math.Clamp(value.MaximumSelectionCharacters, 1, 20_000),
            DefaultTranslationFontSize = double.IsFinite(value.DefaultTranslationFontSize)
                ? Math.Clamp(value.DefaultTranslationFontSize, 12, 34)
                : AppSettings.Default.DefaultTranslationFontSize,
            EnglishTranslationFontFamily = TranslationFontCatalog.NormalizeEnglish(
                value.EnglishTranslationFontFamily),
            ChineseTranslationFontFamily = TranslationFontCatalog.NormalizeChinese(
                value.ChineseTranslationFontFamily),
        };
    }
}
