using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Yita.Settings;
using Yita.Native.Windows;

namespace Yita.Desktop;

internal static class ReferenceTheme
{
    internal static SolidColorBrush Brush(string color) => new(Color.Parse(color));

    internal static void Apply(IResourceDictionary resources, AppSettings settings)
    {
        var palette = ThemeCatalog.Resolve(settings.ColorTheme, settings.CustomAccentColor);
        // These are the original neutral settings colors used by non-Yita accents.
        var keys = new[] { "AppWindowBackgroundBrush", "AppCardBackgroundBrush", "AppCardBorderBrush", "AppFieldBackgroundBrush", "AppFieldBorderBrush", "AppTextBrush", "AppMutedTextBrush", "AppHoverBrush", "AppPressedBrush", "SuccessBrush", "DangerBrush" };
        var neutral = new[] { "#F5F5F7", "#FFFFFF", "#E5E5EA", "#F2F2F7", "#D1D1D6", "#1D1D1F", "#6E6E73", "#E9E9ED", "#DEDEE3", "#16784A", "#B42318" };
        var yita = new[] { "#F5F2EC", "#FFFCF7", "#E3DCD0", "#F4F0E8", "#C7BDAE", "#302D29", "#696158", "#E8EEE7", "#DCE7DE", "#24756B", "#A43E32" };
        for (var i = 0; i < keys.Length; i++) resources[keys[i]] = Brush(palette.PopupBackground == "#FFFCF7" ? yita[i] : neutral[i]);
        resources["InterfaceFont"] = new FontFamily("Microsoft YaHei");
        var motion = ReferenceMotion.Enabled && WindowsDisplayPreferences.AnimationsEnabled;
        resources["PressMotionDuration"] = TimeSpan.FromMilliseconds(motion ? 140 : 0);
        resources["SwitchMotionDuration"] = TimeSpan.FromMilliseconds(motion ? 200 : 0);
        resources["PressedScale"] = motion ? Avalonia.Media.Transformation.TransformOperations.Parse("scale(0.96)")
            : Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");
        resources["AccentBrush"] = Brush(palette.Accent);
        resources["AccentLightBrush"] = Brush(palette.AccentLight);
        resources["AccentTextBrush"] = Brushes.White;
        resources["PopupBackgroundBrush"] = PreviewBrush(settings.PopupVisualStyle, palette);
        resources["PopupBorderBrush"] = Brush(palette.PopupBorder);
        resources["PopupButtonBrush"] = Brush(palette.PopupButton);
        resources["PopupButtonHoverBrush"] = Brush(palette.PopupButtonHover);
        resources["PopupButtonBorderBrush"] = Brush(palette.PopupButtonBorder);
        resources["PopupTextBrush"] = Brush(palette.PopupText);
        resources["PopupMutedBrush"] = Brush(palette.PopupMuted);
        resources["PopupTailBrush"] = PreviewBrush(settings.PopupVisualStyle, palette);
        resources["ExplanationSurfaceBrush"] = Brush(palette.PopupBackground);
        resources["ExplanationHeaderBrush"] = Brush(palette.PopupButton);
        resources["QuestionAnswerSurfaceBrush"] = Brush(palette.PopupBackground);
        resources["QuestionAnswerHeaderBrush"] = Brush(palette.PopupButton);
        resources["QuestionInputBrush"] = Brush(palette.PopupBackground == "#FFFCF7" ? "#F4F0E8" : "#F2F2F7");
        var glass = PopupVisualStyleCatalog.IsBubbleV3(settings.PopupVisualStyle);
        var sculpted = glass || PopupVisualStyleCatalog.IsBubbleV2(settings.PopupVisualStyle);
        resources["PopupSurfaceCornerRadius"] = new CornerRadius(sculpted ? 40 : PopupVisualStyleCatalog.IsBubble(settings.PopupVisualStyle) ? 28 : 18);
        resources["PopupActionBarCornerRadius"] = new CornerRadius(sculpted ? 22 : PopupVisualStyleCatalog.IsBubble(settings.PopupVisualStyle) ? 18 : 14);
        resources["PopupButtonCornerRadius"] = new CornerRadius(sculpted ? 12 : PopupVisualStyleCatalog.IsBubble(settings.PopupVisualStyle) ? 11 : 8);
        resources["PopupGlassEnabled"] = glass;
        resources["PopupTextSurfaceBrush"] = glass ? Brush(palette.PopupBackground) : Brushes.Transparent;
        resources["PopupTextSurfaceCornerRadius"] = new CornerRadius(glass ? 18 : 0);
        resources["PopupTextSurfacePadding"] = new Thickness(glass ? 12 : 0);
        if (glass && palette.PopupBackground != "#FFFCF7")
        {
            var colorful = PopupVisualStyleCatalog.IsBubbleV3Color(settings.PopupVisualStyle);
            resources["PopupBackgroundBrush"] = ReferenceMaterial.Surface(colorful);
            resources["PopupButtonBrush"] = ReferenceMaterial.Toolbar(colorful);
            resources["PopupTextSurfaceBrush"] = ReferenceMaterial.TextSurface(colorful);
            resources["ExplanationSurfaceBrush"] = resources["QuestionAnswerSurfaceBrush"] = ReferenceMaterial.Surface(colorful, true);
            resources["ExplanationHeaderBrush"] = Brush(colorful ? "#24F1F3FC" : "#24EAF1F7");
            resources["QuestionAnswerHeaderBrush"] = Brush(colorful ? "#24F7F7FD" : "#24F4F8FC");
            resources["QuestionInputBrush"] = Brush("#88FFFFFF");
            resources["PopupTextBrush"] = Brush("#17232F");
            resources["PopupMutedBrush"] = Brush("#4D6073");
        }
        if (WindowsDisplayPreferences.HighContrast) ApplyHighContrast(resources);
    }

    private static void ApplyHighContrast(IResourceDictionary resources)
    {
        foreach (var key in new[] { "AppWindowBackgroundBrush", "AppCardBackgroundBrush", "AppFieldBackgroundBrush", "PopupBackgroundBrush", "PopupButtonBrush", "PopupTextSurfaceBrush", "ExplanationSurfaceBrush", "QuestionAnswerSurfaceBrush", "QuestionInputBrush" }) resources[key] = Brush(WindowsDisplayPreferences.SystemColor(5));
        foreach (var key in new[] { "AppCardBorderBrush", "AppFieldBorderBrush", "AppTextBrush", "PopupTextBrush", "PopupBorderBrush", "PopupButtonBorderBrush", "SuccessBrush", "DangerBrush" }) resources[key] = Brush(WindowsDisplayPreferences.SystemColor(8));
        foreach (var key in new[] { "AccentBrush", "AccentLightBrush", "AppHoverBrush", "PopupButtonHoverBrush", "ExplanationHeaderBrush", "QuestionAnswerHeaderBrush" }) resources[key] = Brush(WindowsDisplayPreferences.SystemColor(13));
        resources["AccentTextBrush"] = Brush(WindowsDisplayPreferences.SystemColor(14));
        resources["AppMutedTextBrush"] = resources["PopupMutedBrush"] = Brush(WindowsDisplayPreferences.SystemColor(17));
        resources["PopupGlassEnabled"] = false;
        resources["PopupTextSurfacePadding"] = new Thickness(0);
        foreach (var key in new[] { "PopupSurfaceCornerRadius", "PopupActionBarCornerRadius", "PopupButtonCornerRadius", "PopupTextSurfaceCornerRadius" }) resources[key] = new CornerRadius(0);
    }

    internal static IBrush PreviewBrush(string style, ThemePalette palette)
    {
        if (palette.PopupBackground == "#FFFCF7")
            return Brush(PopupVisualStyleCatalog.IsBubbleV3(style) ? "#60FFF8EC" : palette.PopupBackground);
        if (!PopupVisualStyleCatalog.IsBubble(style)) return Brush(palette.PopupBackground);
        if (PopupVisualStyleCatalog.IsBubbleV3(style))
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = PopupVisualStyleCatalog.IsBubbleV3Color(style)
                    ? [new GradientStop(Color.Parse("#70FFD6E8"), 0), new GradientStop(Color.Parse("#60DDEBFF"), .52), new GradientStop(Color.Parse("#60E7DCFF"), 1)]
                    : [new GradientStop(Color.Parse("#70FFFFFF"), 0), new GradientStop(Color.Parse("#50F1F7FC"), 1)],
            };
        return Brush(PopupVisualStyleCatalog.IsBubbleV2(style) ? "#F2F4FF" : "#EEF7FF");
    }
}
