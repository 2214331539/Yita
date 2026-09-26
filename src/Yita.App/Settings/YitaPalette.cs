using System.Windows;

namespace Yita.Settings;

// Warm near-white reading surfaces; brand colors are reserved for controls and emphasis.
internal static class YitaPalette
{
    internal static IReadOnlyDictionary<string, string> SurfaceColors { get; } = new Dictionary<string, string>
    {
        ["AppWindowBackgroundBrush"] = "#F5F2EC",
        ["AppCardBackgroundBrush"] = "#FFFCF7",
        ["AppCardBorderBrush"] = "#E3DCD0",
        ["AppFieldBackgroundBrush"] = "#F4F0E8",
        ["AppFieldBorderBrush"] = "#C7BDAE",
        ["AppTextBrush"] = "#302D29",
        ["AppMutedTextBrush"] = "#696158",
        ["AppHoverBrush"] = "#E8EEE7",
        ["AppPressedBrush"] = "#DCE7DE",
        ["SuccessBrush"] = "#24756B",
        ["DangerBrush"] = "#A43E32",
        ["PopupBackgroundBrush"] = "#FFFCF7",
        ["PopupBorderBrush"] = "#DDD5C9",
        ["PopupButtonBrush"] = "#EFE9DF",
        ["PopupButtonHoverBrush"] = "#E3EDE7",
        ["PopupButtonBorderBrush"] = "#C7BDAE",
        ["PopupTextBrush"] = "#302D29",
        ["PopupMutedBrush"] = "#696158",
        ["PopupTailBrush"] = "#FFFCF7",
        ["ExplanationSurfaceBrush"] = "#FFFCF7",
        ["ExplanationHeaderBrush"] = "#EFE9DF",
        ["QuestionAnswerSurfaceBrush"] = "#FFFCF7",
        ["QuestionAnswerHeaderBrush"] = "#EFE9DF",
        ["QuestionInputBrush"] = "#F4F0E8",
    };

    internal static bool IsActive(ThemePalette palette) => palette.PopupBackground == "#FFFCF7";

    internal static void Apply(ResourceDictionary resources, string visualStyle)
    {
        foreach (var (key, color) in SurfaceColors)
            resources[key] = ThemeManager.CreateBrush(color);
        resources["PopupHighlightBrush"] = System.Windows.Media.Brushes.Transparent;
        if (PopupVisualStyleCatalog.IsBubbleV3(visualStyle))
        {
            // Keep the outer material translucent, but never let desktop details show behind text.
            resources["PopupBackgroundBrush"] = ThemeManager.CreateBrush("#60FFF8EC");
            resources["PopupTailBrush"] = ThemeManager.CreateBrush("#60FFF8EC");
            resources["PopupTextSurfaceBrush"] = ThemeManager.CreateBrush("#FFFCF7");
        }
    }
}
