using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColors = System.Windows.Media.Colors;
using WpfPoint = System.Windows.Point;
using WpfSystemColors = System.Windows.SystemColors;

namespace Yita.Settings;

internal static class ThemeManager
{
    internal static void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (System.Windows.Application.Current is not { } application)
        {
            return;
        }

        if (SystemParameters.HighContrast)
        {
            ApplyHighContrast(application.Resources);
            return;
        }

        var palette = ThemeCatalog.Resolve(settings.ColorTheme, settings.CustomAccentColor);
        SetBrush(application.Resources, "AppWindowBackgroundBrush", "#F5F5F7");
        SetBrush(application.Resources, "AppCardBackgroundBrush", "#FFFFFF");
        SetBrush(application.Resources, "AppCardBorderBrush", "#E5E5EA");
        SetBrush(application.Resources, "AppFieldBackgroundBrush", "#F2F2F7");
        SetBrush(application.Resources, "AppFieldBorderBrush", "#D1D1D6");
        SetBrush(application.Resources, "AppTextBrush", "#1D1D1F");
        SetBrush(application.Resources, "AppMutedTextBrush", "#6E6E73");
        SetBrush(application.Resources, "AppHoverBrush", "#E9E9ED");
        SetBrush(application.Resources, "AppPressedBrush", "#DEDEE3");
        SetBrush(application.Resources, "SuccessBrush", "#16784A");
        SetBrush(application.Resources, "DangerBrush", "#B42318");
        SetBrush(application.Resources, "AccentBrush", palette.Accent);
        SetBrush(application.Resources, "AccentLightBrush", palette.AccentLight);
        SetBrush(application.Resources, "AccentTextBrush", "#FFFFFF");
        SetBrush(application.Resources, "SwitchKnobBrush", "#FFFFFF");
        ApplyContentHighlightResources(
            application.Resources,
            HighlightPaletteCatalog.Resolve(settings.HighlightPalette));
        var popupVisualStyle = PopupVisualStyleCatalog.Normalize(settings.PopupVisualStyle);
        ApplyPopupResources(application.Resources, palette, popupVisualStyle);
        ApplyExplanationResources(application.Resources, popupVisualStyle);
        if (YitaPalette.IsActive(palette))
            YitaPalette.Apply(application.Resources, popupVisualStyle);
    }

    internal static void ApplyHighContrast(ResourceDictionary resources)
    {
        resources["PopupGlassEnabled"] = false;
        SetBrush(resources, "PopupTextSurfaceBrush", WpfSystemColors.WindowBrush);
        SetCornerRadius(resources, "PopupTextSurfaceCornerRadius", new CornerRadius(0));
        resources["PopupTextSurfacePadding"] = new Thickness(0);
        SetBrush(resources, "AppWindowBackgroundBrush", WpfSystemColors.WindowBrush);
        SetBrush(resources, "AppCardBackgroundBrush", WpfSystemColors.WindowBrush);
        SetBrush(resources, "AppCardBorderBrush", WpfSystemColors.WindowTextBrush);
        SetBrush(resources, "AppFieldBackgroundBrush", WpfSystemColors.WindowBrush);
        SetBrush(resources, "AppFieldBorderBrush", WpfSystemColors.WindowTextBrush);
        SetBrush(resources, "AppTextBrush", WpfSystemColors.WindowTextBrush);
        SetBrush(resources, "AppMutedTextBrush", WpfSystemColors.GrayTextBrush);
        SetBrush(resources, "AppHoverBrush", WpfSystemColors.HighlightBrush);
        SetBrush(resources, "AppPressedBrush", WpfSystemColors.HotTrackBrush);
        SetBrush(resources, "SuccessBrush", WpfSystemColors.WindowTextBrush);
        SetBrush(resources, "DangerBrush", WpfSystemColors.WindowTextBrush);
        SetBrush(resources, "AccentBrush", WpfSystemColors.HighlightBrush);
        SetBrush(resources, "AccentLightBrush", WpfSystemColors.HighlightBrush);
        SetBrush(resources, "AccentTextBrush", WpfSystemColors.HighlightTextBrush);
        SetBrush(resources, "SwitchKnobBrush", WpfSystemColors.HighlightTextBrush);
        SetBrush(resources, "PopupBackgroundBrush", WpfSystemColors.WindowBrush);
        SetBrush(resources, "PopupBorderBrush", WpfSystemColors.WindowTextBrush);
        SetBrush(resources, "PopupButtonBrush", WpfSystemColors.WindowBrush);
        SetBrush(resources, "PopupButtonHoverBrush", WpfSystemColors.HighlightBrush);
        SetBrush(resources, "PopupButtonBorderBrush", WpfSystemColors.WindowTextBrush);
        SetBrush(resources, "PopupTextBrush", WpfSystemColors.WindowTextBrush);
        SetBrush(resources, "PopupMutedBrush", WpfSystemColors.GrayTextBrush);
        SetBrush(resources, "PopupBadgeBrush", WpfSystemColors.HighlightBrush);
        SetBrush(resources, "ContentHighlightPrimaryBrush", WpfSystemColors.HighlightTextBrush);
        SetBrush(resources, "ContentHighlightPrimaryBackgroundBrush", WpfSystemColors.HighlightBrush);
        SetBrush(resources, "ContentHighlightSecondaryBrush", WpfSystemColors.HighlightTextBrush);
        SetBrush(resources, "ContentHighlightSecondaryBackgroundBrush", WpfSystemColors.HighlightBrush);
        SetBrush(resources, "ContentHighlightTertiaryBrush", WpfSystemColors.HighlightTextBrush);
        SetBrush(resources, "ContentHighlightTertiaryBackgroundBrush", WpfSystemColors.HighlightBrush);
        SetBrush(resources, "PopupHighlightBrush", WpfSystemColors.WindowBrush);
        SetBrush(resources, "PopupTailBrush", WpfSystemColors.WindowBrush);
        SetBrush(resources, "PopupTailStrokeBrush", WpfSystemColors.WindowTextBrush);
        SetCornerRadius(resources, "PopupSurfaceCornerRadius", new CornerRadius(0));
        SetCornerRadius(resources, "PopupActionBarCornerRadius", new CornerRadius(0));
        SetCornerRadius(resources, "PopupButtonCornerRadius", new CornerRadius(0));
        SetEffect(resources, "PopupSurfaceShadowEffect", CreateShadow(WpfColors.Transparent, 0, 0, 0));
        SetEffect(resources, "PopupActionBarShadowEffect", CreateShadow(WpfColors.Transparent, 0, 0, 0));
        SetBrush(resources, "ExplanationSurfaceBrush", WpfSystemColors.WindowBrush);
        SetBrush(resources, "ExplanationHeaderBrush", WpfSystemColors.HighlightBrush);
        SetBrush(resources, "QuestionAnswerSurfaceBrush", WpfSystemColors.WindowBrush);
        SetBrush(resources, "QuestionAnswerHeaderBrush", WpfSystemColors.HighlightBrush);
        SetBrush(resources, "QuestionInputBrush", WpfSystemColors.WindowBrush);
    }

    internal static SolidColorBrush CreateBrush(string colorValue)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorValue);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    internal static System.Windows.Media.Brush CreatePopupPreviewSurfaceBrush(
        string? popupVisualStyle,
        ThemePalette palette)
    {
        var style = PopupVisualStyleCatalog.Normalize(popupVisualStyle);
        if (YitaPalette.IsActive(palette))
            return CreateBrush(PopupVisualStyleCatalog.IsBubbleV3(style) ? "#60FFF8EC" : palette.PopupBackground);
        return style switch
        {
            PopupVisualStyleCatalog.BubbleStyleId => CreateBubbleSurfaceBrush(),
            PopupVisualStyleCatalog.BubbleV2StyleId => CreateBubbleV2SurfaceBrush(),
            PopupVisualStyleCatalog.BubbleV3StyleId => LiquidGlassMaterial.Surface,
            PopupVisualStyleCatalog.BubbleV3ColorStyleId => LiquidGlassMaterial.ColorSurface,
            _ => CreateBrush(palette.PopupBackground),
        };
    }

    private static void SetBrush(ResourceDictionary resources, string key, string colorValue)
    {
        resources[key] = CreateBrush(colorValue);
    }

    private static void SetBrush(ResourceDictionary resources, string key, System.Windows.Media.Brush brush)
    {
        resources[key] = brush;
    }

    internal static void ApplyPopupResources(
        ResourceDictionary resources,
        ThemePalette palette,
        string popupVisualStyle)
    {
        var normalizedStyle = PopupVisualStyleCatalog.Normalize(popupVisualStyle);
        resources["PopupGlassEnabled"] = false;
        SetBrush(resources, "PopupTextSurfaceBrush", WpfBrushes.Transparent);
        SetCornerRadius(resources, "PopupTextSurfaceCornerRadius", new CornerRadius(0));
        resources["PopupTextSurfacePadding"] = new Thickness(0);
        if (PopupVisualStyleCatalog.IsBubbleV3(normalizedStyle))
        {
            ApplyBubbleV3Resources(resources, palette, PopupVisualStyleCatalog.IsBubbleV3Color(normalizedStyle));
            return;
        }
        if (string.Equals(normalizedStyle, PopupVisualStyleCatalog.MinimalStyleId, StringComparison.Ordinal))
        {
            SetBrush(resources, "PopupBackgroundBrush", palette.PopupBackground);
            SetBrush(resources, "PopupBorderBrush", palette.PopupBorder);
            SetBrush(resources, "PopupButtonBrush", palette.PopupButton);
            SetBrush(resources, "PopupButtonHoverBrush", palette.PopupButtonHover);
            SetBrush(resources, "PopupButtonBorderBrush", palette.PopupButtonBorder);
            SetBrush(resources, "PopupTextBrush", palette.PopupText);
            SetBrush(resources, "PopupMutedBrush", palette.PopupMuted);
            SetBrush(resources, "PopupBadgeBrush", palette.PopupBadge);
            SetBrush(resources, "PopupHighlightBrush", WpfBrushes.Transparent);
            SetBrush(resources, "PopupTailBrush", palette.PopupBackground);
            SetBrush(resources, "PopupTailStrokeBrush", WpfBrushes.Transparent);
            SetCornerRadius(resources, "PopupSurfaceCornerRadius", new CornerRadius(18));
            SetCornerRadius(resources, "PopupActionBarCornerRadius", new CornerRadius(14));
            SetCornerRadius(resources, "PopupButtonCornerRadius", new CornerRadius(8));
            SetEffect(resources, "PopupSurfaceShadowEffect", CreateShadow(WpfColors.Transparent, 0, 0, 0));
            SetEffect(resources, "PopupActionBarShadowEffect", CreateShadow(WpfColors.Transparent, 0, 0, 0));
            return;
        }

        if (string.Equals(normalizedStyle, PopupVisualStyleCatalog.BubbleV2StyleId, StringComparison.Ordinal))
        {
            ApplyBubbleV2Resources(resources, palette);
            return;
        }

        SetBrush(resources, "PopupBackgroundBrush", CreateBubbleSurfaceBrush());
        SetBrush(resources, "PopupBorderBrush", "#C9D9EA");
        SetBrush(resources, "PopupButtonBrush", "#FCFDFF");
        SetBrush(resources, "PopupButtonHoverBrush", "#EAF3FE");
        SetBrush(resources, "PopupButtonBorderBrush", "#BDD0E4");
        SetBrush(resources, "PopupTextBrush", "#182235");
        SetBrush(resources, "PopupMutedBrush", "#64748B");
        SetBrush(resources, "PopupBadgeBrush", palette.Accent);
        SetBrush(resources, "PopupHighlightBrush", CreateBubbleHighlightBrush());
        SetBrush(resources, "PopupTailBrush", "#EEF7FF");
        SetBrush(resources, "PopupTailStrokeBrush", WpfBrushes.Transparent);
        SetCornerRadius(resources, "PopupSurfaceCornerRadius", new CornerRadius(28));
        SetCornerRadius(resources, "PopupActionBarCornerRadius", new CornerRadius(18));
        SetCornerRadius(resources, "PopupButtonCornerRadius", new CornerRadius(11));
        SetEffect(resources, "PopupSurfaceShadowEffect", CreateShadow(WpfColor.FromRgb(47, 72, 108), 0.19, 22, 7));
        SetEffect(resources, "PopupActionBarShadowEffect", CreateShadow(WpfColor.FromRgb(47, 72, 108), 0.10, 12, 4));
    }

    private static void ApplyContentHighlightResources(
        ResourceDictionary resources,
        HighlightPalette palette)
    {
        SetBrush(resources, "ContentHighlightPrimaryBrush", palette.PrimaryForeground);
        SetBrush(resources, "ContentHighlightPrimaryBackgroundBrush", palette.PrimaryBackground);
        SetBrush(resources, "ContentHighlightSecondaryBrush", palette.SecondaryForeground);
        SetBrush(resources, "ContentHighlightSecondaryBackgroundBrush", palette.SecondaryBackground);
        SetBrush(resources, "ContentHighlightTertiaryBrush", palette.TertiaryForeground);
        SetBrush(resources, "ContentHighlightTertiaryBackgroundBrush", palette.TertiaryBackground);
    }

    private static void ApplyBubbleV2Resources(ResourceDictionary resources, ThemePalette palette)
    {
        SetBrush(resources, "PopupBackgroundBrush", CreateBubbleV2SurfaceBrush());
        SetBrush(resources, "PopupBorderBrush", WpfBrushes.Transparent);
        SetBrush(resources, "PopupButtonBrush", CreateBubbleV2ButtonBrush());
        SetBrush(resources, "PopupButtonHoverBrush", "#EAF0FF");
        SetBrush(resources, "PopupButtonBorderBrush", "#DCE5FA");
        SetBrush(resources, "PopupTextBrush", "#18213A");
        SetBrush(resources, "PopupMutedBrush", "#62708D");
        SetBrush(resources, "PopupBadgeBrush", palette.Accent);
        SetBrush(resources, "PopupHighlightBrush", CreateBubbleV2HighlightBrush());
        SetBrush(resources, "PopupTailBrush", "#F2F4FF");
        SetBrush(resources, "PopupTailStrokeBrush", "#FFFFFF");
        SetCornerRadius(resources, "PopupSurfaceCornerRadius", new CornerRadius(40));
        SetCornerRadius(resources, "PopupActionBarCornerRadius", new CornerRadius(22));
        SetCornerRadius(resources, "PopupButtonCornerRadius", new CornerRadius(12));
        SetEffect(resources, "PopupSurfaceShadowEffect", CreateShadow(WpfColor.FromRgb(46, 67, 105), 0.18, 32, 8));
        SetEffect(resources, "PopupActionBarShadowEffect", CreateShadow(WpfColor.FromRgb(46, 67, 105), 0.12, 18, 5));
    }

    private static void ApplyBubbleV3Resources(ResourceDictionary resources, ThemePalette palette, bool colorful)
    {
        resources["PopupGlassEnabled"] = true;
        SetBrush(resources, "PopupTextSurfaceBrush", colorful ? LiquidGlassMaterial.ColorTextSurface : LiquidGlassMaterial.TextSurface);
        SetCornerRadius(resources, "PopupTextSurfaceCornerRadius", new CornerRadius(18));
        // Inset the complete text viewport, including scrollbars and selection,
        // so none of it can paint over the card's rounded corners.
        resources["PopupTextSurfacePadding"] = new Thickness(12);
        SetBrush(resources, "PopupBackgroundBrush", colorful ? LiquidGlassMaterial.ColorSurface : LiquidGlassMaterial.Surface);
        SetBrush(resources, "PopupBorderBrush", WpfBrushes.Transparent);
        SetBrush(resources, "PopupButtonBrush", colorful ? LiquidGlassMaterial.ColorToolbar : LiquidGlassMaterial.Toolbar);
        SetBrush(resources, "PopupButtonHoverBrush", "#D9FFFFFF");
        SetBrush(resources, "PopupButtonBorderBrush", "#899CAE");
        SetBrush(resources, "PopupTextBrush", "#17232F");
        SetBrush(resources, "PopupMutedBrush", "#4D6073");
        SetBrush(resources, "PopupBadgeBrush", palette.Accent);
        SetBrush(resources, "PopupHighlightBrush", WpfBrushes.Transparent);
        SetBrush(resources, "PopupTailBrush", colorful ? "#50F2F4FF" : "#50F1F7FC");
        SetBrush(resources, "PopupTailStrokeBrush", "#E6FFFFFF");
        SetCornerRadius(resources, "PopupSurfaceCornerRadius", new CornerRadius(40));
        SetCornerRadius(resources, "PopupActionBarCornerRadius", new CornerRadius(22));
        SetCornerRadius(resources, "PopupButtonCornerRadius", new CornerRadius(12));
        SetEffect(resources, "PopupSurfaceShadowEffect", CreateShadow(WpfColor.FromRgb(33, 51, 73), 0.20, 28, 7));
        SetEffect(resources, "PopupActionBarShadowEffect", CreateShadow(WpfColor.FromRgb(33, 51, 73), 0.13, 18, 5));
    }

    private static void ApplyExplanationResources(ResourceDictionary resources, string popupVisualStyle)
    {
        if (PopupVisualStyleCatalog.IsBubbleV3(popupVisualStyle))
        {
            var colorful = PopupVisualStyleCatalog.IsBubbleV3Color(popupVisualStyle);
            var surface = colorful ? LiquidGlassMaterial.ColorAuxiliarySurface : LiquidGlassMaterial.AuxiliarySurface;
            SetBrush(resources, "ExplanationSurfaceBrush", surface);
            SetBrush(resources, "ExplanationHeaderBrush", colorful ? "#24F1F3FC" : "#24EAF1F7");
            SetBrush(resources, "QuestionAnswerSurfaceBrush", surface);
            SetBrush(resources, "QuestionAnswerHeaderBrush", colorful ? "#24F7F7FD" : "#24F4F8FC");
            SetBrush(resources, "QuestionInputBrush", "#88FFFFFF");
            return;
        }
        if (PopupVisualStyleCatalog.IsBubbleV2(popupVisualStyle))
        {
            SetBrush(resources, "ExplanationSurfaceBrush", CreateAuxiliarySurfaceBrush(
                "#FFFFFF", "#F3F8FF", "#FBF5FF"));
            SetBrush(resources, "ExplanationHeaderBrush", "#F6F8FD");
            SetBrush(resources, "QuestionAnswerSurfaceBrush", CreateAuxiliarySurfaceBrush(
                "#FFFFFF", "#F0F7FF", "#F9F2FF"));
            SetBrush(resources, "QuestionAnswerHeaderBrush", "#F5F7FD");
            SetBrush(resources, "QuestionInputBrush", "#FCFDFF");
            return;
        }

        SetBrush(resources, "ExplanationSurfaceBrush", CreateAuxiliarySurfaceBrush(
            "#FFFFFF", "#F5F8FC", "#EEF3F9"));
        SetBrush(resources, "ExplanationHeaderBrush", "#F3F6FA");
        SetBrush(resources, "QuestionAnswerSurfaceBrush", CreateAuxiliarySurfaceBrush(
            "#FFFFFF", "#F3F7FC", "#ECF2F9"));
        SetBrush(resources, "QuestionAnswerHeaderBrush", "#F1F5FA");
        SetBrush(resources, "QuestionInputBrush", "#FFFFFF");
    }

    private static LinearGradientBrush CreateAuxiliarySurfaceBrush(
        string start,
        string middle,
        string end)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new WpfPoint(0, 0),
            EndPoint = new WpfPoint(1, 1),
        };
        brush.GradientStops.Add(new GradientStop(
            (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(start),
            0));
        brush.GradientStops.Add(new GradientStop(
            (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(middle),
            0.58));
        brush.GradientStops.Add(new GradientStop(
            (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(end),
            1));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateBubbleSurfaceBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new WpfPoint(0, 0),
            EndPoint = new WpfPoint(1, 1),
        };
        brush.GradientStops.Add(new GradientStop(WpfColor.FromRgb(255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(WpfColor.FromRgb(238, 248, 255), 0.54));
        brush.GradientStops.Add(new GradientStop(WpfColor.FromRgb(246, 241, 255), 1));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateBubbleHighlightBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new WpfPoint(0.1, 0),
            EndPoint = new WpfPoint(0.9, 1),
        };
        brush.GradientStops.Add(new GradientStop(WpfColor.FromArgb(118, 255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(WpfColor.FromArgb(42, 255, 255, 255), 0.34));
        brush.GradientStops.Add(new GradientStop(WpfColor.FromArgb(0, 255, 255, 255), 0.72));
        brush.Freeze();
        return brush;
    }

    private static DrawingBrush CreateBubbleV2SurfaceBrush()
    {
        const double canvasSize = 100;
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(
            CreateBubbleV2BaseBrush(),
            null,
            new RectangleGeometry(new Rect(0, 0, canvasSize, canvasSize))));
        drawing.Children.Add(new GeometryDrawing(
            CreateSoftAuraBrush(WpfColor.FromArgb(118, 255, 164, 223)),
            null,
            new EllipseGeometry(new WpfPoint(84, 20), 40, 34)));
        drawing.Children.Add(new GeometryDrawing(
            CreateSoftAuraBrush(WpfColor.FromArgb(96, 115, 220, 255)),
            null,
            new EllipseGeometry(new WpfPoint(15, 80), 43, 39)));
        drawing.Children.Add(new GeometryDrawing(
            CreateSoftAuraBrush(WpfColor.FromArgb(84, 184, 143, 255)),
            null,
            new EllipseGeometry(new WpfPoint(86, 83), 42, 38)));
        drawing.Children.Add(new GeometryDrawing(
            CreateSoftAuraBrush(WpfColor.FromArgb(168, 255, 255, 255)),
            null,
            new EllipseGeometry(new WpfPoint(21, 13), 34, 24)));
        drawing.Freeze();

        var brush = new DrawingBrush(drawing)
        {
            Viewbox = new Rect(0, 0, canvasSize, canvasSize),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1),
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
        };
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateBubbleV2BaseBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new WpfPoint(0, 0),
            EndPoint = new WpfPoint(1, 1),
        };
        brush.GradientStops.Add(new GradientStop(WpfColor.FromRgb(255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(WpfColor.FromRgb(241, 248, 255), 0.44));
        brush.GradientStops.Add(new GradientStop(WpfColor.FromRgb(246, 241, 255), 1));
        return brush;
    }

    private static RadialGradientBrush CreateSoftAuraBrush(WpfColor color)
    {
        var brush = new RadialGradientBrush
        {
            Center = new WpfPoint(0.5, 0.5),
            GradientOrigin = new WpfPoint(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        brush.GradientStops.Add(new GradientStop(color, 0));
        brush.GradientStops.Add(new GradientStop(WpfColor.FromArgb(0, color.R, color.G, color.B), 0.82));
        return brush;
    }

    private static DrawingBrush CreateBubbleV2HighlightBrush()
    {
        const double canvasSize = 100;
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(
            CreateSoftAuraBrush(WpfColor.FromArgb(124, 255, 255, 255)),
            null,
            new EllipseGeometry(new WpfPoint(30, 8), 52, 28)));
        drawing.Freeze();

        var brush = new DrawingBrush(drawing)
        {
            Viewbox = new Rect(0, 0, canvasSize, canvasSize),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1),
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
        };
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateBubbleV2ButtonBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new WpfPoint(0, 0),
            EndPoint = new WpfPoint(0, 1),
        };
        brush.GradientStops.Add(new GradientStop(WpfColor.FromRgb(255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(WpfColor.FromRgb(247, 249, 255), 1));
        brush.Freeze();
        return brush;
    }

    private static DropShadowEffect CreateShadow(System.Windows.Media.Color color, double opacity, double blurRadius, double shadowDepth)
    {
        var effect = new DropShadowEffect
        {
            Color = color,
            Opacity = opacity,
            BlurRadius = blurRadius,
            ShadowDepth = shadowDepth,
            Direction = 270,
        };
        effect.Freeze();
        return effect;
    }

    private static void SetCornerRadius(ResourceDictionary resources, string key, CornerRadius cornerRadius)
    {
        resources[key] = cornerRadius;
    }

    private static void SetEffect(ResourceDictionary resources, string key, Effect effect)
    {
        resources[key] = effect;
    }
}
