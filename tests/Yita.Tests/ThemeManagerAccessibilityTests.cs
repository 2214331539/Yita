using System.Windows;
using Yita.Settings;
using WpfSystemColors = System.Windows.SystemColors;

namespace Yita.Tests;

public sealed class ThemeManagerAccessibilityTests
{
    [Fact]
    public void ApplyHighContrast_UsesSystemColorsForTextSelectionAndSurfaces()
    {
        var resources = new ResourceDictionary();

        ThemeManager.ApplyHighContrast(resources);

        Assert.Same(WpfSystemColors.WindowBrush, resources["PopupBackgroundBrush"]);
        Assert.Same(WpfSystemColors.WindowTextBrush, resources["PopupTextBrush"]);
        Assert.Same(WpfSystemColors.HighlightBrush, resources["AccentBrush"]);
        Assert.Same(WpfSystemColors.HighlightTextBrush, resources["AccentTextBrush"]);
        Assert.Same(WpfSystemColors.HighlightTextBrush, resources["ContentHighlightPrimaryBrush"]);
        Assert.Same(WpfSystemColors.HighlightBrush, resources["ContentHighlightPrimaryBackgroundBrush"]);
        Assert.Same(WpfSystemColors.HighlightTextBrush, resources["ContentHighlightSecondaryBrush"]);
        Assert.Same(WpfSystemColors.HighlightBrush, resources["ContentHighlightSecondaryBackgroundBrush"]);
        Assert.Same(WpfSystemColors.HighlightTextBrush, resources["ContentHighlightTertiaryBrush"]);
        Assert.Same(WpfSystemColors.HighlightBrush, resources["ContentHighlightTertiaryBackgroundBrush"]);
        Assert.Same(WpfSystemColors.WindowBrush, resources["AppWindowBackgroundBrush"]);
        Assert.Same(WpfSystemColors.WindowTextBrush, resources["AppTextBrush"]);
        Assert.Same(WpfSystemColors.WindowBrush, resources["ExplanationSurfaceBrush"]);
        Assert.Same(WpfSystemColors.HighlightBrush, resources["ExplanationHeaderBrush"]);
        Assert.Same(WpfSystemColors.WindowBrush, resources["QuestionAnswerSurfaceBrush"]);
        Assert.Same(WpfSystemColors.HighlightBrush, resources["QuestionAnswerHeaderBrush"]);
        Assert.Same(WpfSystemColors.WindowBrush, resources["QuestionInputBrush"]);
    }
}
