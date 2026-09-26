using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yita.Settings;

namespace Yita.Tests;

public sealed class LiquidGlassMaterialTests
{
    [Fact]
    public void AllMaterialBrushesAreFrozenAndShared()
    {
        foreach (var brush in new System.Windows.Media.Brush[]
                 {
                     LiquidGlassMaterial.Surface, LiquidGlassMaterial.AuxiliarySurface,
                     LiquidGlassMaterial.ColorSurface, LiquidGlassMaterial.ColorAuxiliarySurface,
                     LiquidGlassMaterial.TextSurface, LiquidGlassMaterial.ColorTextSurface,
                     LiquidGlassMaterial.ColorToolbar,
                     LiquidGlassMaterial.Toolbar, LiquidGlassMaterial.Edge,
                     LiquidGlassMaterial.InnerEdge, LiquidGlassMaterial.TopReflection,
                     LiquidGlassMaterial.BottomReflection,
                 })
        {
            Assert.True(brush.IsFrozen);
            Assert.Equal(1, brush.Opacity);
        }
        Assert.Same(LiquidGlassMaterial.Surface,
            ThemeManager.CreatePopupPreviewSurfaceBrush("bubble-v3", ThemeCatalog.Resolve("ocean", "")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TranslationAndAuxiliarySurfacesAreSeeThrough(bool colorful)
    {
        var surface = GetBaseGradient(colorful ? LiquidGlassMaterial.ColorSurface : LiquidGlassMaterial.Surface);
        Assert.All(surface.GradientStops, stop => Assert.InRange((int)stop.Color.A, 48, 80));
        Assert.All(GetBaseGradient(colorful ? LiquidGlassMaterial.ColorAuxiliarySurface : LiquidGlassMaterial.AuxiliarySurface).GradientStops,
            stop => Assert.InRange((int)stop.Color.A, 40, 64));
        Assert.All((colorful ? LiquidGlassMaterial.ColorToolbar : LiquidGlassMaterial.Toolbar).GradientStops,
            stop => Assert.InRange((int)stop.Color.A, 96, 144));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RenderedGlassPixelsRemainTransparentAfterAllTintLayers(bool colorful, bool auxiliary)
    {
        var brush = colorful
            ? auxiliary ? LiquidGlassMaterial.ColorAuxiliarySurface : LiquidGlassMaterial.ColorSurface
            : auxiliary ? LiquidGlassMaterial.AuxiliarySurface : LiquidGlassMaterial.Surface;
        byte[]? pixels = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                {
                    context.DrawRectangle(brush, null, new Rect(0, 0, 100, 100));
                }
                var bitmap = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                pixels = new byte[100 * 100 * 4];
                bitmap.CopyPixels(pixels, 100 * 4, 0);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Glass pixel render timed out.");
        Assert.Null(failure);
        Assert.NotNull(pixels);
        for (var y = 5; y < 95; y += 5)
        {
            for (var x = 5; x < 95; x += 5)
            {
                // Check actual composited alpha, not just individual brush stops.
                Assert.InRange((int)pixels[(y * 100 + x) * 4 + 3], 35, 140);
            }
        }
    }

    [Fact]
    public void ColorGlassPreservesBubble2AuraColorsWithoutChangingBubble2Material()
    {
        var palette = ThemeCatalog.Resolve("ocean", "");
        var original = Assert.IsType<DrawingBrush>(ThemeManager.CreatePopupPreviewSurfaceBrush("bubble-v2", palette));
        Assert.All(GetBaseGradient(original).GradientStops, stop => Assert.Equal(255, stop.Color.A));
        Assert.Equal(GetAuraColors(original), GetAuraColors(LiquidGlassMaterial.ColorSurface));
        Assert.NotEqual(GetAuraColors(LiquidGlassMaterial.Surface), GetAuraColors(LiquidGlassMaterial.ColorSurface));
        Assert.Same(LiquidGlassMaterial.ColorSurface, ThemeManager.CreatePopupPreviewSurfaceBrush("bubble-v3-color", palette));
    }

    [Fact]
    public void SwitchingBetweenGlassVariantsUpdatesSurfaceAndToolbarIndependently()
    {
        var resources = new ResourceDictionary();
        var palette = ThemeCatalog.Resolve("ocean", "");
        ThemeManager.ApplyPopupResources(resources, palette, "bubble-v3-color");
        Assert.True((bool)resources["PopupGlassEnabled"]);
        Assert.Same(LiquidGlassMaterial.ColorSurface, resources["PopupBackgroundBrush"]);
        Assert.Same(LiquidGlassMaterial.ColorToolbar, resources["PopupButtonBrush"]);

        ThemeManager.ApplyPopupResources(resources, palette, "bubble-v3");
        Assert.True((bool)resources["PopupGlassEnabled"]);
        Assert.Same(LiquidGlassMaterial.Surface, resources["PopupBackgroundBrush"]);
        Assert.Same(LiquidGlassMaterial.Toolbar, resources["PopupButtonBrush"]);
    }

    [Theory]
    [InlineData("bubble-v3")]
    [InlineData("bubble-v3-color")]
    public void GlassStylesUseSoftOpaqueRoundedTextCardsInsideTransparentShells(string style)
    {
        var resources = new ResourceDictionary();
        ThemeManager.ApplyPopupResources(resources, ThemeCatalog.Resolve("ocean", ""), style);

        var card = Assert.IsType<LinearGradientBrush>(resources["PopupTextSurfaceBrush"]);
        Assert.Same(style == "bubble-v3-color" ? LiquidGlassMaterial.ColorTextSurface : LiquidGlassMaterial.TextSurface, card);
        Assert.All(card.GradientStops, stop =>
        {
            Assert.Equal(255, stop.Color.A);
            Assert.NotEqual(System.Windows.Media.Colors.White, stop.Color);
            Assert.InRange((int)stop.Color.R, 240, 251);
            Assert.InRange((int)stop.Color.G, 240, 251);
            Assert.InRange((int)stop.Color.B, 240, 251);
        });
        Assert.True(card.IsFrozen);
        Assert.Equal(1, card.Opacity);
        Assert.Equal(new CornerRadius(18), resources["PopupTextSurfaceCornerRadius"]);
        Assert.Equal(new Thickness(12), resources["PopupTextSurfacePadding"]);
        Assert.All(GetBaseGradient(Assert.IsType<DrawingBrush>(resources["PopupBackgroundBrush"])).GradientStops,
            stop => Assert.InRange((int)stop.Color.A, 48, 80));
    }

    [Theory]
    [InlineData("bubble-v3", "minimal")]
    [InlineData("bubble-v3", "bubble")]
    [InlineData("bubble-v3", "bubble-v2")]
    [InlineData("bubble-v3-color", "minimal")]
    [InlineData("bubble-v3-color", "bubble")]
    [InlineData("bubble-v3-color", "bubble-v2")]
    public void SwitchingToOlderStylesRemovesGlassDecoration(string initialStyle, string nextStyle)
    {
        var resources = new ResourceDictionary();
        var palette = ThemeCatalog.Resolve("ocean", "");
        ThemeManager.ApplyPopupResources(resources, palette, initialStyle);
        Assert.True((bool)resources["PopupGlassEnabled"]);
        Assert.Equal(new CornerRadius(40), resources["PopupSurfaceCornerRadius"]);

        ThemeManager.ApplyPopupResources(resources, palette, nextStyle);
        Assert.False((bool)resources["PopupGlassEnabled"]);
        Assert.NotSame(LiquidGlassMaterial.Surface, resources["PopupBackgroundBrush"]);
        Assert.NotSame(LiquidGlassMaterial.ColorSurface, resources["PopupBackgroundBrush"]);
        Assert.Equal(0, Assert.IsType<SolidColorBrush>(resources["PopupTextSurfaceBrush"]).Color.A);
        Assert.Equal(new CornerRadius(0), resources["PopupTextSurfaceCornerRadius"]);
        Assert.Equal(new Thickness(0), resources["PopupTextSurfacePadding"]);
    }

    [Theory]
    [InlineData("bubble-v3")]
    [InlineData("bubble-v3-color")]
    public void HighContrastDisablesGlassAndReplacesTranslucentSurface(string style)
    {
        var resources = new ResourceDictionary();
        ThemeManager.ApplyPopupResources(resources, ThemeCatalog.Resolve("ocean", ""), style);

        ThemeManager.ApplyHighContrast(resources);

        Assert.False((bool)resources["PopupGlassEnabled"]);
        Assert.Same(System.Windows.SystemColors.WindowBrush, resources["PopupBackgroundBrush"]);
        Assert.Same(System.Windows.SystemColors.WindowBrush, resources["ExplanationSurfaceBrush"]);
        Assert.Same(System.Windows.SystemColors.WindowBrush, resources["PopupTextSurfaceBrush"]);
        Assert.Equal(new CornerRadius(0), resources["PopupTextSurfaceCornerRadius"]);
        Assert.Equal(new Thickness(0), resources["PopupTextSurfacePadding"]);
    }

    private static LinearGradientBrush GetBaseGradient(DrawingBrush brush) =>
        Assert.IsType<LinearGradientBrush>(
            Assert.IsType<GeometryDrawing>(Assert.IsType<DrawingGroup>(brush.Drawing).Children[0]).Brush);

    private static string[] GetAuraColors(DrawingBrush brush) =>
        Assert.IsType<DrawingGroup>(brush.Drawing).Children
            .OfType<GeometryDrawing>()
            .Select(drawing => drawing.Brush)
            .OfType<RadialGradientBrush>()
            .Select(aura => aura.GradientStops[0].Color)
            .Where(color => color.R != color.G || color.G != color.B)
            .Select(color => $"{color.R:X2}{color.G:X2}{color.B:X2}")
            .ToArray();
}
