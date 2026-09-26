using Yita.Windows;

namespace Yita.Tests;

public sealed class PopupAutoSizeCalculatorTests
{
    [Fact]
    public void Calculate_ReservesSpaceForGlassTextCardWithoutReducingTextArea()
    {
        const string text = "One line.\nAnother line.\nA third line.";
        var original = PopupAutoSizeCalculator.Calculate(text, 16.5, 1920, 1080);
        var card = PopupAutoSizeCalculator.Calculate(text, 16.5, 1920, 1080,
            additionalHorizontalPadding: 24, additionalVerticalPadding: 24);

        Assert.Equal(original.Width + 24, card.Width, precision: 1);
        Assert.Equal(original.Height + 24, card.Height, precision: 1);
    }

    [Fact]
    public void Calculate_GlassPaddingStillRespectsScreenLimits()
    {
        var card = PopupAutoSizeCalculator.Calculate(new string('译', 5000), 34, 640, 420,
            additionalHorizontalPadding: 24, additionalVerticalPadding: 24);

        Assert.InRange(card.Width, 1, 640);
        Assert.InRange(card.Height, 1, 420);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-24)]
    public void Calculate_IgnoresInvalidExtraPadding(double padding)
    {
        var original = PopupAutoSizeCalculator.Calculate("Short translation.", 16.5, 1920, 1080);
        var invalid = PopupAutoSizeCalculator.Calculate("Short translation.", 16.5, 1920, 1080,
            additionalHorizontalPadding: padding, additionalVerticalPadding: padding);

        Assert.Equal(original, invalid);
    }

    [Fact]
    public void Calculate_GrowsWithTranslationLength()
    {
        var shortText = PopupAutoSizeCalculator.Calculate("Short translation.", 16.5, 1920, 1080);
        var mediumText = PopupAutoSizeCalculator.Calculate(new string('译', 120), 16.5, 1920, 1080);
        var longText = PopupAutoSizeCalculator.Calculate(new string('译', 1000), 16.5, 1920, 1080);

        Assert.True(mediumText.Width > shortText.Width);
        Assert.True(longText.Width >= mediumText.Width);
        Assert.True(longText.Height > mediumText.Height);
    }

    [Fact]
    public void Calculate_RespectsAvailableScreenAndPreferredCaps()
    {
        var largeScreen = PopupAutoSizeCalculator.Calculate(new string('译', 5000), 34, 3840, 2160);
        var compactScreen = PopupAutoSizeCalculator.Calculate(new string('译', 5000), 34, 640, 420);

        Assert.Equal(PopupAutoSizeCalculator.PreferredMaximumWidth, largeScreen.Width);
        Assert.Equal(PopupAutoSizeCalculator.PreferredMaximumHeight, largeScreen.Height);
        Assert.InRange(compactScreen.Width, 1, 640);
        Assert.InRange(compactScreen.Height, 1, 420);
    }

    [Fact]
    public void Calculate_AccountsForExplicitLineBreaksAndFontSize()
    {
        const string singleLine = "one two three four five six seven eight";
        const string multipleLines = "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight";
        var compact = PopupAutoSizeCalculator.Calculate(singleLine, 14, 1920, 1080);
        var multiline = PopupAutoSizeCalculator.Calculate(multipleLines, 14, 1920, 1080);
        var largeFont = PopupAutoSizeCalculator.Calculate(multipleLines, 28, 1920, 1080);

        Assert.True(multiline.Height > compact.Height);
        Assert.True(largeFont.Height > multiline.Height);
    }

    [Fact]
    public void Calculate_NeverClipsTheActionBarWidth()
    {
        var size = PopupAutoSizeCalculator.Calculate("OK", 16.5, 1920, 1080, actionBarWidth: 560);

        Assert.True(size.Width >= 560);
    }

    [Fact]
    public void Calculate_ReservesHeightForPersistentWindowAndFontControls()
    {
        var size = PopupAutoSizeCalculator.Calculate("Short translation.", 16.5, 1920, 1080);

        Assert.True(size.Height >= PopupAutoSizeCalculator.MinimumHeight);
    }
}
