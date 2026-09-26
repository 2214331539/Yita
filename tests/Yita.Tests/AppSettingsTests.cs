using System.Text.Json;
using Yita.Settings;

namespace Yita.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void Serialization_DoesNotContainApiKey()
    {
        var settings = AppSettings.Default with { DeepSeekApiKey = "secret-value" };

        var json = JsonSerializer.Serialize(settings);

        Assert.DoesNotContain("secret-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DeepSeekApiKey", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PopupOpacity", json, StringComparison.Ordinal);
    }

    [Fact]
    public void OldSettingsJson_DefaultsToOceanTheme()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}");

        Assert.NotNull(settings);
        Assert.Equal(ThemeCatalog.DefaultThemeId, settings.ColorTheme);
        Assert.Equal(ThemeCatalog.DefaultCustomAccent, settings.CustomAccentColor);
        Assert.Equal(PopupVisualStyleCatalog.DefaultStyleId, settings.PopupVisualStyle);
        Assert.False(settings.StartWithWindows);
        Assert.False(settings.UseClipboardFallback);
        Assert.Equal(16.5, settings.DefaultTranslationFontSize);
        Assert.Equal(8000, settings.MaximumSelectionCharacters);
        Assert.Equal(UiLanguageCatalog.DefaultLanguageId, settings.UiLanguage);
        Assert.Equal(TranslationFontCatalog.DefaultEnglishFontFamily, settings.EnglishTranslationFontFamily);
        Assert.Equal(TranslationFontCatalog.DefaultChineseFontFamily, settings.ChineseTranslationFontFamily);
        Assert.False(settings.UseSelectionContext);
        Assert.Equal(TranslationPreferenceCatalog.DefaultModeId, settings.TranslationMode);
        Assert.Equal(TranslationPreferenceCatalog.DefaultToneId, settings.TranslationTone);
        Assert.Empty(settings.PersonalGlossary);
    }
}
