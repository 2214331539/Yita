using Yita.Settings;

namespace Yita.Core.Settings;

public sealed record YitaSettings
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool StartWithSystem { get; init; }
    public string UiLanguage { get; init; } = "en";
    public bool UseClipboardFallback { get; init; } = true;
    public int SelectionCompatibilityVersion { get; init; }
    public bool UseWpsPdfCompatibility { get; init; } = true;
    public bool UseSelectionContext { get; init; }
    public int SelectionDelayMilliseconds { get; init; } = 80;
    public int MaximumSelectionCharacters { get; init; } = 8000;
    public string SourceLanguage { get; init; } = "自动检测";
    public string TargetLanguage { get; init; } = "简体中文";
    public string TargetLanguageMode { get; init; } = "auto";
    public string TranslationMode { get; init; } = "balanced";
    public string TranslationTone { get; init; } = "natural";
    public string PersonalGlossary { get; init; } = string.Empty;
    public string ColorTheme { get; init; } = "yita";
    public string CustomAccentColor { get; init; } = "#24756B";
    public string PopupVisualStyle { get; init; } = "minimal";
    public bool ReduceMotion { get; init; }
    public double DefaultTranslationFontSize { get; init; } = 16.5;
    public string EnglishTranslationFontFamily { get; init; } = TranslationFontCatalog.DefaultEnglishFontFamily;
    public string ChineseTranslationFontFamily { get; init; } = TranslationFontCatalog.DefaultChineseFontFamily;
    public bool AiHistoryEnabled { get; init; }
    public string AiHistoryDirectory { get; init; } = string.Empty;
    public string AiSummaryRange { get; init; } = "Today";
    public double? PopupOffsetX { get; init; }
    public double? PopupOffsetY { get; init; }
    public string ProviderId { get; init; } = "deepseek";
    public string DeepSeekEndpoint { get; init; } = "https://api.deepseek.com";
    public string DeepSeekModel { get; init; } = "deepseek-v4-flash";
    public static YitaSettings Default { get; } = new()
        { SchemaVersion = CurrentSchemaVersion, SelectionCompatibilityVersion = 1 };

    internal AppSettings ToOriginal(string apiKey = "") => SettingsStore.NormalizeSettings(new AppSettings
    {
        TypographyVersion = 1, BrandPaletteVersion = 1,
        UiLanguage = UiLanguage, IsEnabled = IsEnabled, StartWithWindows = StartWithSystem,
        UseClipboardFallback = UseClipboardFallback, UseWpsPdfCompatibility = UseWpsPdfCompatibility,
        SelectionDelayMilliseconds = SelectionDelayMilliseconds, MaximumSelectionCharacters = MaximumSelectionCharacters,
        SourceLanguage = NormalizeLanguage(SourceLanguage), TargetLanguage = NormalizeLanguage(TargetLanguage), TargetLanguageMode = TargetLanguageMode,
        UseSelectionContext = UseSelectionContext, TranslationMode = TranslationMode, TranslationTone = TranslationTone,
        PersonalGlossary = PersonalGlossary, ColorTheme = ColorTheme, CustomAccentColor = CustomAccentColor,
        PopupVisualStyle = PopupVisualStyle, DefaultTranslationFontSize = DefaultTranslationFontSize,
        EnglishTranslationFontFamily = EnglishTranslationFontFamily, ChineseTranslationFontFamily = ChineseTranslationFontFamily,
        AiHistoryEnabled = AiHistoryEnabled, AiHistoryDirectory = AiHistoryDirectory,
        AiSummaryRange = Enum.TryParse<SummaryRange>(AiSummaryRange, out var range) ? range : SummaryRange.Today,
        ProviderId = ProviderId, DeepSeekEndpoint = DeepSeekEndpoint, DeepSeekModel = DeepSeekModel,
        DeepSeekApiKey = apiKey,
    });

    private static string NormalizeLanguage(string language) => language switch
    {
        "auto" => "自动检测", "English" => "英语", "简体中文" or "Chinese" or "zh-CN" => "简体中文",
        "Japanese" or "日本語" => "日语", "Korean" => "韩语", "French" => "法语", "German" => "德语",
        "Spanish" => "西班牙语", _ => language,
    };

    internal static YitaSettings FromOriginal(AppSettings value) => new()
    {
        UiLanguage = value.UiLanguage, IsEnabled = value.IsEnabled, StartWithSystem = value.StartWithWindows,
        UseClipboardFallback = value.UseClipboardFallback, UseWpsPdfCompatibility = value.UseWpsPdfCompatibility,
        SelectionDelayMilliseconds = value.SelectionDelayMilliseconds, MaximumSelectionCharacters = value.MaximumSelectionCharacters,
        SourceLanguage = value.SourceLanguage, TargetLanguage = value.TargetLanguage, TargetLanguageMode = value.TargetLanguageMode,
        UseSelectionContext = value.UseSelectionContext, TranslationMode = value.TranslationMode, TranslationTone = value.TranslationTone,
        PersonalGlossary = value.PersonalGlossary, ColorTheme = value.ColorTheme, CustomAccentColor = value.CustomAccentColor,
        PopupVisualStyle = value.PopupVisualStyle, DefaultTranslationFontSize = value.DefaultTranslationFontSize,
        EnglishTranslationFontFamily = value.EnglishTranslationFontFamily, ChineseTranslationFontFamily = value.ChineseTranslationFontFamily,
        AiHistoryEnabled = value.AiHistoryEnabled, AiHistoryDirectory = value.AiHistoryDirectory,
        AiSummaryRange = value.AiSummaryRange.ToString(), ProviderId = value.ProviderId,
        DeepSeekEndpoint = value.DeepSeekEndpoint, DeepSeekModel = value.DeepSeekModel,
    };
}

public interface ISettingsStore
{
    Task<YitaSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task ValidateWriteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

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
