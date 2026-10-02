using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Yita.Desktop;

public sealed class LiquidGlassRim : Control
{
    public static readonly StyledProperty<bool> IsGlassEnabledProperty = AvaloniaProperty.Register<LiquidGlassRim, bool>(nameof(IsGlassEnabled));
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty = AvaloniaProperty.Register<LiquidGlassRim, CornerRadius>(nameof(CornerRadius));
    static LiquidGlassRim() => AffectsRender<LiquidGlassRim>(IsGlassEnabledProperty, CornerRadiusProperty);
    public LiquidGlassRim() { IsHitTestVisible = false; Focusable = false; }
    public bool IsGlassEnabled { get => GetValue(IsGlassEnabledProperty); set => SetValue(IsGlassEnabledProperty, value); }
    public CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public override void Render(DrawingContext context)
    {
        if (!IsGlassEnabled || Bounds.Width < 12 || Bounds.Height < 12) return;
        var outer = new Rect(.75, .75, Bounds.Width - 1.5, Bounds.Height - 1.5);
        var radius = Math.Min(CornerRadius.TopLeft, Math.Min(outer.Width, outer.Height) / 2);
        using (context.PushGeometryClip(new RectangleGeometry(outer, radius, radius)))
        {
            context.DrawRectangle(ReferenceMaterial.TopReflection, null, new Rect(0, 0, Bounds.Width, Math.Min(34, Bounds.Height * .22)));
            var lowerHeight = Math.Min(16, Bounds.Height * .16);
            context.DrawRectangle(ReferenceMaterial.BottomReflection, null, new Rect(0, Bounds.Height - lowerHeight, Bounds.Width, lowerHeight));
        }
        context.DrawRectangle(null, new Pen(ReferenceMaterial.Edge, 1.15), outer, radius, radius);
        context.DrawRectangle(null, new Pen(ReferenceMaterial.InnerEdge, .65), new Rect(2.5, 2.5, Bounds.Width - 5, Bounds.Height - 5), Math.Max(0, radius - 1.75), Math.Max(0, radius - 1.75));
    }
}
