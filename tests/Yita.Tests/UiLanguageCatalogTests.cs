using Yita.Settings;

namespace Yita.Tests;

public sealed class UiLanguageCatalogTests
{
    [Fact]
    public void DefaultLanguage_IsEnglish()
    {
        Assert.Equal("en", UiLanguageCatalog.DefaultLanguageId);
        Assert.Contains(UiLanguageCatalog.Options, option => option.Id == "en");
        Assert.Contains(UiLanguageCatalog.Options, option => option.Id == "zh-CN");
    }

    [Theory]
    [InlineData(" EN ", "en")]
    [InlineData("english", "en")]
    [InlineData("zh-cn", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("简体中文", "zh-CN")]
    [InlineData("unsupported", "en")]
    [InlineData(null, "en")]
    public void Normalize_ReturnsCanonicalWhitelistedValue(string? value, string expected)
    {
        Assert.Equal(expected, UiLanguageCatalog.Normalize(value));
    }

    [Theory]
    [InlineData("尚未配置 DeepSeek API Key，请从托盘打开设置。", "The DeepSeek API key is not configured. Open Settings from the tray.")]
    [InlineData("DeepSeek API 返回 401：Invalid API key", "DeepSeek API returned 401: Invalid API key")]
    [InlineData("DeepSeek 在 20 秒内未返回首段译文，请检查网络后重试。", "DeepSeek did not return translated text within 20 seconds. Check the network and try again.")]
    [InlineData("DeepSeek 流式响应停顿超过 15 秒，已取消。", "The DeepSeek stream stopped responding for 15 seconds and was canceled.")]
    [InlineData("DeepSeek 因连续网络故障暂时暂停，请在 5 秒后重试。", "DeepSeek is temporarily paused after repeated network failures. Wait a few seconds and try again.")]
    [InlineData("Plain English API error", "Plain English API error")]
    public void LocalizeProviderError_EnglishUi_NeverLeaksChinesePrompts(string input, string expected)
    {
        Assert.Equal(expected, UiLanguageCatalog.LocalizeProviderError("en", input));
    }

    [Fact]
    public void LocalizeProviderError_ChineseUi_PreservesChineseMessage()
    {
        const string message = "无法连接 DeepSeek API，请检查网络和 Endpoint。";

        Assert.Equal(message, UiLanguageCatalog.LocalizeProviderError("zh-CN", message));
    }

    [Theory]
    [InlineData("en", "Translation failed. Check your DeepSeek settings and network.")]
    [InlineData("zh-CN", "翻译失败，请检查 DeepSeek 设置。")]
    public void LocalizeProviderError_EmptyMessage_UsesSelectedUiLanguage(string language, string expected)
    {
        Assert.Equal(expected, UiLanguageCatalog.LocalizeProviderError(language, string.Empty));
    }
}
