using Avalonia;
using Avalonia.Media;

namespace Yita.Desktop;

// Brush geometry and stops match the original LiquidGlassMaterial.
internal static class ReferenceMaterial
{
    internal static readonly IBrush TopReflection = Gradient(0, 0, 0, 1, ("#50FFFFFF", 0), ("#14FFFFFF", .40), ("#00FFFFFF", 1));
    internal static readonly IBrush BottomReflection = Gradient(0, 0, 0, 1, ("#00FFFFFF", 0), ("#0CFFFFFF", .48), ("#70FFFFFF", 1));
    internal static readonly IBrush Edge = Gradient(.12, 0, .85, 1, ("#FAFFFFFF", 0), ("#B5FFFFFF", .30), ("#42798CA3", .56), ("#B5FFFFFF", .78), ("#ECFFFFFF", 1));
    internal static readonly IBrush InnerEdge = Gradient(0, 0, 1, 1, ("#687D91AA", 0), ("#1CFFFFFF", .34), ("#0CFFFFFF", .68), ("#709BACBF", 1));
    internal static IBrush TextSurface(bool colorful) => colorful
        ? Gradient(0, 0, .65, 1, ("#FAF8FB", 0), ("#F3F0F7", 1))
        : Gradient(0, 0, .35, 1, ("#F7F9FB", 0), ("#F1F4F8", 1));
    internal static IBrush Toolbar(bool colorful) => colorful
        ? Gradient(0, 0, 1, 1, ("#90FFFFFF", 0), ("#70EAF6FF", .42), ("#64EADDFB", 1))
        : Gradient(0, 0, 0, 1, ("#90FFFFFF", 0), ("#70F3F7FC", .42), ("#64DEE8F3", 1));
    internal static IBrush Surface(bool colorful, bool auxiliary = false)
    {
        var drawing = new DrawingGroup();
        var background = colorful
            ? auxiliary ? Gradient(0, 0, 1, 1, ("#40FFFFFF", 0), ("#30F5F9FF", .44), ("#38FBF5FF", 1))
                : Gradient(0, 0, 1, 1, ("#50FFFFFF", 0), ("#38F1F8FF", .44), ("#40F6F1FF", 1))
            : auxiliary ? Gradient(0, 0, .8, 1, ("#40FCFEFF", 0), ("#30F1F6FA", .54), ("#38E8EFF6", 1))
                : Gradient(0, 0, .25, 1, ("#50FFFFFF", 0), ("#3CF0F6FC", .38), ("#38E0EAF4", .72), ("#40EAF0F7", 1));
        drawing.Children.Add(new GeometryDrawing { Brush = background, Geometry = new RectangleGeometry(new Rect(0, 0, 100, 100)) });
        AddAura(drawing, "#24FFFFFF", 25, 4, 70, 24);
        if (colorful)
        {
            AddAura(drawing, auxiliary ? "#26FFA4DF" : "#3CFFA4DF", 84, 20, 40, 34);
            AddAura(drawing, auxiliary ? "#2473DCFF" : "#3873DCFF", 15, 80, 43, 39);
            AddAura(drawing, auxiliary ? "#22B88FFF" : "#32B88FFF", 86, 83, 42, 38);
        }
        else { AddAura(drawing, "#167FC3E1", 5, 86, 31, 46); AddAura(drawing, "#148C9DD7", 97, 68, 28, 45); }
        AddAura(drawing, "#18FFFFFF", 66, 98, 54, 17);
        // The original drawing is clipped to a 100x100 brush viewbox.
        var clipped = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, 100, 100)) };
        clipped.Children.Add(drawing);
        return new DrawingBrush(clipped) { SourceRect = new RelativeRect(0, 0, 100, 100, RelativeUnit.Absolute), Stretch = Stretch.Fill };
    }
    private static void AddAura(DrawingGroup group, string value, double x, double y, double rx, double ry)
    {
        var color = Color.Parse(value);
        var brush = new RadialGradientBrush { Center = new RelativePoint(.5, .5, RelativeUnit.Relative), GradientOrigin = new RelativePoint(.5, .5, RelativeUnit.Relative), RadiusX = new RelativeScalar(.5, RelativeUnit.Relative), RadiusY = new RelativeScalar(.5, RelativeUnit.Relative) };
        brush.GradientStops.Add(new GradientStop(color, 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
        group.Children.Add(new GeometryDrawing { Brush = brush, Geometry = new EllipseGeometry(new Rect(x - rx, y - ry, rx * 2, ry * 2)) });
    }
    internal static LinearGradientBrush Gradient(double x1, double y1, double x2, double y2, params (string Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new RelativePoint(x1, y1, RelativeUnit.Relative), EndPoint = new RelativePoint(x2, y2, RelativeUnit.Relative) };
        foreach (var (color, offset) in stops) brush.GradientStops.Add(new GradientStop(Color.Parse(color), offset));
        return brush;
    }
}
