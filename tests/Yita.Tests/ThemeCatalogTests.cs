using Yita.Settings;

namespace Yita.Tests;

public sealed class ThemeCatalogTests
{
    [Fact]
    public void Resolve_KnownTheme_ReturnsItsAccentColor()
    {
        var palette = ThemeCatalog.Resolve("violet", null);

        Assert.Equal("#7C3AED", palette.Accent);
        Assert.Equal(7, palette.PopupBackground.Length);
        Assert.StartsWith("#", palette.PopupBackground, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("#12abEF", "#12ABEF")]
    [InlineData("12abef", "#12ABEF")]
    public void TryNormalizeHexColor_NormalizesValidInput(string input, string expected)
    {
        var isValid = ThemeCatalog.TryNormalizeHexColor(input, out var normalized);

        Assert.True(isValid);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("#1234")]
    [InlineData("not-a-color")]
    [InlineData("")]
    public void TryNormalizeHexColor_RejectsInvalidInput(string input)
    {
        Assert.False(ThemeCatalog.TryNormalizeHexColor(input, out _));
    }

    [Fact]
    public void Resolve_CustomTheme_UsesCustomAccent()
    {
        var palette = ThemeCatalog.Resolve(ThemeCatalog.CustomThemeId, "#ABCDEF");

        Assert.Equal("#ABCDEF", palette.Accent);
    }
}
