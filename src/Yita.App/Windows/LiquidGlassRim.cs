using System.Windows;
using System.Windows.Media;
using Yita.Settings;
using Pen = System.Windows.Media.Pen;

namespace Yita.Windows;

/// <summary>
/// Decorative only: never receives input or changes the content's measure.
/// Its fine optical rim is redrawn only when geometry or theme changes.
/// </summary>
internal sealed class LiquidGlassRim : FrameworkElement
{
    public static readonly DependencyProperty IsGlassEnabledProperty = DependencyProperty.Register(
        nameof(IsGlassEnabled), typeof(bool), typeof(LiquidGlassRim),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(LiquidGlassRim),
        new FrameworkPropertyMetadata(new CornerRadius(40), FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly Pen OuterPen = CreatePen(LiquidGlassMaterial.Edge, 1.15);
    private static readonly Pen InnerPen = CreatePen(LiquidGlassMaterial.InnerEdge, 0.65);

    public LiquidGlassRim()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = false;
    }

    public bool IsGlassEnabled
    {
        get => (bool)GetValue(IsGlassEnabledProperty);
        set => SetValue(IsGlassEnabledProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!IsGlassEnabled || ActualWidth < 12 || ActualHeight < 12)
        {
            return;
        }

        var outer = new Rect(0.75, 0.75, ActualWidth - 1.5, ActualHeight - 1.5);
        var radius = Math.Min(CornerRadius.TopLeft, Math.Min(outer.Width, outer.Height) / 2);
        var clip = new RectangleGeometry(outer, radius, radius);
        drawingContext.PushClip(clip);
        drawingContext.DrawRectangle(LiquidGlassMaterial.TopReflection, null,
            new Rect(0, 0, ActualWidth, Math.Min(34, ActualHeight * 0.22)));
        var lowerHeight = Math.Min(16, ActualHeight * 0.16);
        drawingContext.DrawRectangle(LiquidGlassMaterial.BottomReflection, null,
            new Rect(0, ActualHeight - lowerHeight, ActualWidth, lowerHeight));
        drawingContext.Pop();
        drawingContext.DrawRoundedRectangle(null, OuterPen, outer, radius, radius);
        var inner = new Rect(2.5, 2.5, ActualWidth - 5, ActualHeight - 5);
        var innerRadius = Math.Max(0, radius - 1.75);
        drawingContext.DrawRoundedRectangle(null, InnerPen, inner, innerRadius, innerRadius);
    }

    private static Pen CreatePen(System.Windows.Media.Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
