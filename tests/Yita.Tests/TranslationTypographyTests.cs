using Yita.Windows;

namespace Yita.Tests;

public sealed class TranslationTypographyTests
{
    [Fact]
    public void Segment_MixedText_AssignsChineseAndLatinFonts()
    {
        var segments = TranslationTypography.Segment("中文 Hello 世界");

        Assert.Collection(
            segments,
            segment =>
            {
                Assert.Equal("中文 ", segment.Text);
                Assert.True(segment.UsesChineseFont);
            },
            segment =>
            {
                Assert.Equal("Hello ", segment.Text);
                Assert.False(segment.UsesChineseFont);
            },
            segment =>
            {
                Assert.Equal("世界", segment.Text);
                Assert.True(segment.UsesChineseFont);
            });
    }

    [Fact]
    public void Segment_ChinesePunctuation_UsesChineseFont()
    {
        var segment = Assert.Single(TranslationTypography.Segment("你好，世界。"));

        Assert.True(segment.UsesChineseFont);
    }

    [Fact]
    public void Segment_EnglishText_UsesLatinFont()
    {
        var segment = Assert.Single(TranslationTypography.Segment("Hello, world!"));

        Assert.False(segment.UsesChineseFont);
    }
}
