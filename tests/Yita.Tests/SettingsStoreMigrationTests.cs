using System.Text.Json;
using System.IO;
using Yita.Settings;

namespace Yita.Tests;

public sealed class SettingsStoreMigrationTests
{
    [Fact]
    public void BrandPaletteUpgrade_ChangesOldDefaultsButPreservesExplicitChoices()
    {
        var legacy = new AppSettings { ColorTheme = "ocean", HighlightPalette = "clarity" };
        var upgraded = SettingsStore.NormalizeSettings(legacy);
        Assert.Equal("yita", upgraded.ColorTheme);
        Assert.Equal("yita", upgraded.HighlightPalette);
        Assert.Equal(1, upgraded.BrandPaletteVersion);
        var chosen = SettingsStore.NormalizeSettings(upgraded with { ColorTheme = "ocean", HighlightPalette = "clarity" });
        Assert.Equal("ocean", chosen.ColorTheme);
        Assert.Equal("clarity", chosen.HighlightPalette);
        var custom = SettingsStore.NormalizeSettings(legacy with { ColorTheme = "custom", CustomAccentColor = "#336699", HighlightPalette = "warm" });
        Assert.Equal("custom", custom.ColorTheme);
        Assert.Equal("#336699", custom.CustomAccentColor);
        Assert.Equal("warm", custom.HighlightPalette);
    }

    [Fact]
    public void TypographyUpgrade_UpdatesLegacyDefaultsOnceAndPreservesLaterChoices()
    {
        var legacy = new AppSettings
        {
            EnglishTranslationFontFamily = "Times New Roman",
            ChineseTranslationFontFamily = "SimHei",
            DefaultTranslationFontSize = 19,
        };
        var upgraded = SettingsStore.NormalizeSettings(legacy);
        Assert.Equal("Segoe UI", upgraded.EnglishTranslationFontFamily);
        Assert.Equal("Microsoft YaHei UI", upgraded.ChineseTranslationFontFamily);
        Assert.Equal(19, upgraded.DefaultTranslationFontSize);
        Assert.Equal(1, upgraded.TypographyVersion);
        var chosen = SettingsStore.NormalizeSettings(upgraded with
        {
            EnglishTranslationFontFamily = "Times New Roman",
            ChineseTranslationFontFamily = "SimHei",
        });
        Assert.Equal("Times New Roman", chosen.EnglishTranslationFontFamily);
        Assert.Equal("SimHei", chosen.ChineseTranslationFontFamily);
        var custom = SettingsStore.NormalizeSettings(legacy with { EnglishTranslationFontFamily = "Georgia" });
        Assert.Equal("Georgia", custom.EnglishTranslationFontFamily);
        Assert.Equal("SimHei", custom.ChineseTranslationFontFamily);
    }

    [Theory]
    [InlineData(" BUBBLE-V3 ", "bubble-v3")]
    [InlineData(" BUBBLE-V3-COLOR ", "bubble-v3-color")]
    public void NormalizeSettings_GlassStyle_RoundTripsWithoutChangingPrivacyPreferences(string value, string expected)
    {
        var json = JsonSerializer.Serialize(AppSettings.Default with { PopupVisualStyle = value });
        var normalized = SettingsStore.NormalizeSettings(JsonSerializer.Deserialize<AppSettings>(json));

        Assert.Equal(expected, normalized.PopupVisualStyle);
        Assert.False(normalized.UseClipboardFallback);
        Assert.False(normalized.AiHistoryEnabled);
        Assert.Empty(normalized.DeepSeekApiKey);
    }

    [Fact]
    public void LoadPreferences_MalformedJsonReportsFailureWithoutReadingCredential()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Yita-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ malformed");
        try
        {
            var store = new SettingsStore(new NeverReadApiKeyStore(), path);

            var settings = store.LoadPreferences();

            Assert.True(store.SettingsReadFailed);
            Assert.Equal("deepseek", settings.ProviderId);
            Assert.Empty(settings.DeepSeekApiKey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NormalizeSettings_OldJson_AddsNewDefaultsWithoutChangingExistingValues()
    {
        const string oldJson = """
            {
              "IsEnabled": false,
              "SelectionDelayMilliseconds": 125,
              "ProviderId": "mock"
            }
            """;
        var oldSettings = JsonSerializer.Deserialize<AppSettings>(oldJson);

        var migrated = SettingsStore.NormalizeSettings(oldSettings);

        Assert.False(migrated.IsEnabled);
        Assert.Equal(125, migrated.SelectionDelayMilliseconds);
        Assert.Equal("mock", migrated.ProviderId);
        Assert.False(migrated.UseClipboardFallback);
        Assert.Equal("en", migrated.UiLanguage);
        Assert.Equal("Segoe UI", migrated.EnglishTranslationFontFamily);
        Assert.Equal("Microsoft YaHei UI", migrated.ChineseTranslationFontFamily);
        Assert.False(migrated.UseSelectionContext);
        Assert.Equal("balanced", migrated.TranslationMode);
        Assert.Equal("natural", migrated.TranslationTone);
        Assert.Equal(PopupVisualStyleCatalog.DefaultStyleId, migrated.PopupVisualStyle);
        Assert.Equal(HighlightPaletteCatalog.DefaultPaletteId, migrated.HighlightPalette);
        Assert.False(migrated.AiHistoryEnabled);
        Assert.Empty(migrated.AiHistoryDirectory);
        Assert.Equal(SummaryRange.Today, migrated.AiSummaryRange);
    }

    [Fact]
    public void NormalizeSettings_BadValues_FallBackAndLeaveApiKeyUntouched()
    {
        var settings = AppSettings.Default with
        {
            UiLanguage = "xx-invalid",
            EnglishTranslationFontFamily = "Unknown English Font",
            ChineseTranslationFontFamily = "Unknown Chinese Font",
            TranslationMode = "turbo",
            TranslationTone = "dramatic",
            PopupVisualStyle = "unrecognized-style",
            HighlightPalette = "invalid-palette",
            PersonalGlossary = "  API => 接口  ",
            DeepSeekApiKey = "credential-secret",
            AiHistoryDirectory = "  C:\\History  ",
            AiSummaryRange = (SummaryRange)999,
        };

        var normalized = SettingsStore.NormalizeSettings(settings);

        Assert.Equal("en", normalized.UiLanguage);
        Assert.Equal("Segoe UI", normalized.EnglishTranslationFontFamily);
        Assert.Equal("Microsoft YaHei UI", normalized.ChineseTranslationFontFamily);
        Assert.Equal("balanced", normalized.TranslationMode);
        Assert.Equal("natural", normalized.TranslationTone);
        Assert.Equal(PopupVisualStyleCatalog.DefaultStyleId, normalized.PopupVisualStyle);
        Assert.Equal(HighlightPaletteCatalog.DefaultPaletteId, normalized.HighlightPalette);
        Assert.Equal("API => 接口", normalized.PersonalGlossary);
        Assert.Equal("credential-secret", normalized.DeepSeekApiKey);
        Assert.Equal("C:\\History", normalized.AiHistoryDirectory);
        Assert.Equal(SummaryRange.Today, normalized.AiSummaryRange);
    }

    [Fact]
    public void NormalizeSettings_Aliases_AreMigratedToCanonicalStoredNames()
    {
        var settings = AppSettings.Default with
        {
            UiLanguage = "zh",
            EnglishTranslationFontFamily = "SourceSansPro",
            ChineseTranslationFontFamily = "黑体",
        };

        var normalized = SettingsStore.NormalizeSettings(settings);

        Assert.Equal("zh-CN", normalized.UiLanguage);
        Assert.Equal("Source Sans Pro", normalized.EnglishTranslationFontFamily);
        Assert.Equal("SimHei", normalized.ChineseTranslationFontFamily);
    }

    [Fact]
    public void NormalizeSettings_NullProviderValues_FallBackToSafeDefaults()
    {
        var settings = AppSettings.Default with
        {
            ProviderId = null!,
            DeepSeekEndpoint = null!,
            DeepSeekModel = null!,
            SourceLanguage = null!,
            TargetLanguage = null!,
            TargetLanguageMode = null!,
        };

        var normalized = SettingsStore.NormalizeSettings(settings);

        Assert.Equal("deepseek", normalized.ProviderId);
        Assert.Equal("https://api.deepseek.com", normalized.DeepSeekEndpoint);
        Assert.Equal("deepseek-v4-flash", normalized.DeepSeekModel);
        Assert.Equal("自动检测", normalized.SourceLanguage);
        Assert.Equal("简体中文", normalized.TargetLanguage);
        Assert.Equal("auto", normalized.TargetLanguageMode);
    }

    private sealed class NeverReadApiKeyStore : IApiKeyStore
    {
        public string ReadApiKey() => throw new InvalidOperationException("Must not be called.");

        public void SaveApiKey(string apiKey) => throw new InvalidOperationException("Must not be called.");
    }
}
