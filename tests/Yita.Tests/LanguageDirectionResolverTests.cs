using System.Text.Json;
using Yita.Settings;
using Yita.Translation;

namespace Yita.Tests;

public sealed class LanguageDirectionResolverTests
{
    [Theory]
    [InlineData("这是一段中文。", LanguageDirectionResolver.English)]
    [InlineData("中文 with English", LanguageDirectionResolver.Chinese)]
    [InlineData("Hello world", LanguageDirectionResolver.Chinese)]
    [InlineData("12345", LanguageDirectionResolver.Chinese)]
    [InlineData("𠀀", LanguageDirectionResolver.English)]
    public void ResolveTargetLanguage_InAutoMode_UsesExpectedDirection(string source, string expected)
    {
        var settings = AppSettings.Default with { TargetLanguageMode = "auto" };

        var target = LanguageDirectionResolver.ResolveTargetLanguage(source, settings);

        Assert.Equal(expected, target);
    }

    [Fact]
    public void ResolveTargetLanguage_InFixedMode_UsesConfiguredTarget()
    {
        var settings = AppSettings.Default with
        {
            TargetLanguageMode = "fixed",
            TargetLanguage = "日语",
        };

        var target = LanguageDirectionResolver.ResolveTargetLanguage("中文", settings);

        Assert.Equal("日语", target);
    }

    [Theory]
    [InlineData(LanguageDirectionResolver.Chinese, LanguageDirectionResolver.English)]
    [InlineData(LanguageDirectionResolver.English, LanguageDirectionResolver.Chinese)]
    public void GetOppositeTarget_ReturnsOtherLanguage(string current, string expected)
    {
        Assert.Equal(expected, LanguageDirectionResolver.GetOppositeTarget(current));
    }

    [Fact]
    public void OldSettingsJson_DefaultsToAutomaticRouting()
    {
        const string oldJson = """
            {"TargetLanguage":"简体中文"}
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(oldJson);

        Assert.NotNull(settings);
        Assert.Equal("auto", settings.TargetLanguageMode);
    }
}
