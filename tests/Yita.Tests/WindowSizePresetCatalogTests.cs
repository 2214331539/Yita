using Yita.Windows;

namespace Yita.Tests;

public sealed class WindowSizePresetCatalogTests
{
    [Fact]
    public void TranslationScalePresets_GrowInBothDimensions()
    {
        var small = WindowSizePresetCatalog.ForTranslation(WindowSizePreset.Small);
        var medium = WindowSizePresetCatalog.ForTranslation(WindowSizePreset.Medium);
        var large = WindowSizePresetCatalog.ForTranslation(WindowSizePreset.Large);

        Assert.True(small.Width < medium.Width);
        Assert.True(small.Height < medium.Height);
        Assert.True(medium.Width < large.Width);
        Assert.True(medium.Height < large.Height);
    }

    [Fact]
    public void TranslationShapePresets_HaveExpectedOrientation()
    {
        var wide = WindowSizePresetCatalog.ForTranslation(WindowSizePreset.Wide);
        var tall = WindowSizePresetCatalog.ForTranslation(WindowSizePreset.Tall);
        var square = WindowSizePresetCatalog.ForTranslation(WindowSizePreset.Square);

        Assert.True(wide.Width > wide.Height);
        Assert.True(tall.Height > tall.Width);
        Assert.Equal(square.Width, square.Height);
    }

    [Fact]
    public void ConversationScalePresets_GrowInBothDimensions()
    {
        var small = WindowSizePresetCatalog.ForConversation(WindowSizePreset.Small);
        var medium = WindowSizePresetCatalog.ForConversation(WindowSizePreset.Medium);
        var large = WindowSizePresetCatalog.ForConversation(WindowSizePreset.Large);

        Assert.True(small.Width < medium.Width);
        Assert.True(small.Height < medium.Height);
        Assert.True(medium.Width < large.Width);
        Assert.True(medium.Height < large.Height);
    }

    [Fact]
    public void ConversationShapePresets_HaveExpectedOrientation()
    {
        var wide = WindowSizePresetCatalog.ForConversation(WindowSizePreset.Wide);
        var tall = WindowSizePresetCatalog.ForConversation(WindowSizePreset.Tall);
        var square = WindowSizePresetCatalog.ForConversation(WindowSizePreset.Square);

        Assert.True(wide.Width > wide.Height);
        Assert.True(tall.Height > tall.Width);
        Assert.Equal(square.Width, square.Height);
    }
}
