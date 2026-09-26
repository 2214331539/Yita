using Yita.Settings;

namespace Yita.Tests;

public sealed class TranslationPreferenceCatalogTests
{
    [Theory]
    [InlineData("FAST", "fast")]
    [InlineData(" precise ", "precise")]
    [InlineData("unknown", "balanced")]
    [InlineData(null, "balanced")]
    public void NormalizeMode_ReturnsStableValue(string? value, string expected)
    {
        Assert.Equal(expected, TranslationPreferenceCatalog.NormalizeMode(value));
    }

    [Theory]
    [InlineData("FORMAL", "formal")]
    [InlineData(" technical ", "technical")]
    [InlineData("unknown", "natural")]
    [InlineData(null, "natural")]
    public void NormalizeTone_ReturnsStableValue(string? value, string expected)
    {
        Assert.Equal(expected, TranslationPreferenceCatalog.NormalizeTone(value));
    }
}
