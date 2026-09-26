using Yita.Selection;

namespace Yita.Tests;

public sealed class TextNormalizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("  Hello\r\nworld  ", "Hello\nworld")]
    [InlineData("a\0b\rc", "ab\nc")]
    public void Normalize_ReturnsExpectedText(string? input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Normalize(input));
    }
}
