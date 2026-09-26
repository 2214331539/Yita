using Yita.Settings;
using Yita.Translation;

namespace Yita.Tests;

public sealed class HighlightMarkupTests
{
    [Fact]
    public void Parse_ValidMarkers_ExposesCleanTextAndThreeSemanticLevels()
    {
        var parsed = HighlightMarkup.Parse(
            "A [[h1:clear purpose]] with [[h2:careful context]] and [[h3:one caveat]].");

        Assert.Equal("A clear purpose with careful context and one caveat.", parsed.PlainText);
        Assert.Collection(
            parsed.Segments,
            segment => Assert.Equal(("A ", HighlightKind.None), (segment.Text, segment.Kind)),
            segment => Assert.Equal(("clear purpose", HighlightKind.Primary), (segment.Text, segment.Kind)),
            segment => Assert.Equal((" with ", HighlightKind.None), (segment.Text, segment.Kind)),
            segment => Assert.Equal(("careful context", HighlightKind.Secondary), (segment.Text, segment.Kind)),
            segment => Assert.Equal((" and ", HighlightKind.None), (segment.Text, segment.Kind)),
            segment => Assert.Equal(("one caveat", HighlightKind.Tertiary), (segment.Text, segment.Kind)),
            segment => Assert.Equal((".", HighlightKind.None), (segment.Text, segment.Kind)));
        Assert.True(parsed.HasHighlights);
    }

    [Fact]
    public void Parse_IncompleteOrUnsafeMarker_HidesProtocolAndFallsBackToPlainText()
    {
        var incomplete = HighlightMarkup.Parse("Read [[h1:the current selection");
        var multiline = HighlightMarkup.Parse("[[h2:first line\nsecond line]]");

        Assert.Equal("Read the current selection", incomplete.PlainText);
        Assert.All(incomplete.Segments, segment => Assert.Equal(HighlightKind.None, segment.Kind));
        Assert.Equal("first line\nsecond line", multiline.PlainText);
        Assert.All(multiline.Segments, segment => Assert.Equal(HighlightKind.None, segment.Kind));
    }

    [Fact]
    public void Prefix_PreservesOnlyVisibleHighlightRanges()
    {
        var parsed = HighlightMarkup.Parse("[[h1:important phrase]] after");

        var prefix = parsed.Prefix("important".Length);

        Assert.Equal("important", prefix.PlainText);
        var segment = Assert.Single(prefix.Segments);
        Assert.Equal(HighlightKind.Primary, segment.Kind);
    }

    [Fact]
    public void EnsureEmphasis_PreservesProviderHighlightsAndPlainText()
    {
        var parsed = HighlightMarkup.Parse("A [[h2:carefully chosen phrase]] remains clean when copied.");

        var emphasized = HighlightMarkup.EnsureEmphasis(parsed);

        Assert.True(emphasized.HasSamePresentation(parsed));
        Assert.Equal("A carefully chosen phrase remains clean when copied.", emphasized.PlainText);
        Assert.Contains(emphasized.Segments, segment => segment.Kind == HighlightKind.Secondary);
    }

    [Fact]
    public void EnsureEmphasis_PlainStructuredContentHighlightsLabelsWithoutChangingCopyText()
    {
        const string text = "释义：直接而清楚。\n要点：表达自然。\n语境：适合正式说明。";

        var emphasized = HighlightMarkup.EnsureEmphasis(HighlightMarkup.Parse(text));

        Assert.Equal(text, emphasized.PlainText);
        Assert.True(emphasized.HasHighlights);
        Assert.Contains(emphasized.Segments, segment => segment.Text == "释义：" && segment.Kind == HighlightKind.Primary);
        Assert.Contains(emphasized.Segments, segment => segment.Text == "要点：" && segment.Kind == HighlightKind.Secondary);
        Assert.Contains(emphasized.Segments, segment => segment.Text == "语境：" && segment.Kind == HighlightKind.Tertiary);
    }

    [Fact]
    public void EnsureEmphasis_PlainShortAnswerAddsVisualCueWithoutChangingText()
    {
        const string text = "Concise answer.";

        var emphasized = HighlightMarkup.EnsureEmphasis(HighlightMarkup.Parse(text));

        Assert.Equal(text, emphasized.PlainText);
        Assert.True(emphasized.HasHighlights);
    }

    [Theory]
    [InlineData("morandi", HighlightPaletteCatalog.MorandiPaletteId)]
    [InlineData(" OCEAN ", HighlightPaletteCatalog.OceanPaletteId)]
    [InlineData("unknown", HighlightPaletteCatalog.DefaultPaletteId)]
    public void HighlightPalette_NormalizesToSupportedPalette(string source, string expected)
    {
        Assert.Equal(expected, HighlightPaletteCatalog.Normalize(source));
    }
}
