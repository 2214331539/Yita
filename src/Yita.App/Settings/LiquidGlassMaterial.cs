using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace Yita.Settings;

/// <summary>
/// See-through glass surfaces made entirely from frozen vector brushes.
/// Only the material has alpha; text stays opaque. No desktop capture, backdrop
/// sampling, custom shaders, or continuous rendering is involved.
/// </summary>
internal static class LiquidGlassMaterial
{
    internal static DrawingBrush Surface { get; } = CreateSurface(auxiliary: false);
    // The popup hides its underlying translation while explaining; this material
    // can stay transparent rather than painting an opaque cover over that text.
    internal static DrawingBrush AuxiliarySurface { get; } = CreateSurface(auxiliary: true);
    internal static DrawingBrush ColorSurface { get; } = CreateSurface(auxiliary: false, colorful: true);
    internal static DrawingBrush ColorAuxiliarySurface { get; } = CreateSurface(auxiliary: true, colorful: true);
    // Opaque, gently tinted reading cards keep desktop details out of the text
    // while matching each surrounding glass palette without a stark white edge.
    internal static LinearGradientBrush TextSurface { get; } = Gradient(
        new Point(0, 0), new Point(0.35, 1),
        ("#F7F9FB", 0), ("#F1F4F8", 1));
    internal static LinearGradientBrush ColorTextSurface { get; } = Gradient(
        new Point(0, 0), new Point(0.65, 1),
        ("#FAF8FB", 0), ("#F3F0F7", 1));
    internal static LinearGradientBrush Toolbar { get; } = Gradient(
        new Point(0, 0), new Point(0, 1),
        ("#90FFFFFF", 0), ("#70F3F7FC", 0.42), ("#64DEE8F3", 1));
    internal static LinearGradientBrush ColorToolbar { get; } = Gradient(
        new Point(0, 0), new Point(1, 1),
        ("#90FFFFFF", 0), ("#70EAF6FF", 0.42), ("#64EADDFB", 1));
    internal static LinearGradientBrush Edge { get; } = Gradient(
        new Point(0.12, 0), new Point(0.85, 1),
        ("#FAFFFFFF", 0), ("#B5FFFFFF", 0.30), ("#42798CA3", 0.56),
        ("#B5FFFFFF", 0.78), ("#ECFFFFFF", 1));
    internal static LinearGradientBrush InnerEdge { get; } = Gradient(
        new Point(0, 0), new Point(1, 1),
        ("#687D91AA", 0), ("#1CFFFFFF", 0.34), ("#0CFFFFFF", 0.68), ("#709BACBF", 1));
    internal static LinearGradientBrush TopReflection { get; } = Gradient(
        new Point(0, 0), new Point(0, 1),
        ("#50FFFFFF", 0), ("#14FFFFFF", 0.40), ("#00FFFFFF", 1));
    internal static LinearGradientBrush BottomReflection { get; } = Gradient(
        new Point(0, 0), new Point(0, 1),
        ("#00FFFFFF", 0), ("#0CFFFFFF", 0.48), ("#70FFFFFF", 1));

    private static DrawingBrush CreateSurface(bool auxiliary, bool colorful = false)
    {
        var drawing = new DrawingGroup
        {
            ClipGeometry = new RectangleGeometry(new Rect(0, 0, 100, 100)),
        };
        var baseBrush = colorful
            ? auxiliary
                ? Gradient(new Point(0, 0), new Point(1, 1),
                    ("#40FFFFFF", 0), ("#30F5F9FF", 0.44), ("#38FBF5FF", 1))
                : Gradient(new Point(0, 0), new Point(1, 1),
                    ("#50FFFFFF", 0), ("#38F1F8FF", 0.44), ("#40F6F1FF", 1))
            : auxiliary
                ? Gradient(new Point(0, 0), new Point(0.8, 1),
                    ("#40FCFEFF", 0), ("#30F1F6FA", 0.54), ("#38E8EFF6", 1))
                : Gradient(new Point(0, 0), new Point(0.25, 1),
                    ("#50FFFFFF", 0), ("#3CF0F6FC", 0.38),
                    ("#38E0EAF4", 0.72), ("#40EAF0F7", 1));
        drawing.Children.Add(new GeometryDrawing(baseBrush, null,
            new RectangleGeometry(new Rect(0, 0, 100, 100))));
        AddAura(drawing, "#24FFFFFF", new Point(25, 4), 70, 24);
        if (colorful)
        {
            // Preserve Bubble 2.0's pink/cyan/lilac color family and placement.
            AddAura(drawing, auxiliary ? "#26FFA4DF" : "#3CFFA4DF", new Point(84, 20), 40, 34);
            AddAura(drawing, auxiliary ? "#2473DCFF" : "#3873DCFF", new Point(15, 80), 43, 39);
            AddAura(drawing, auxiliary ? "#22B88FFF" : "#32B88FFF", new Point(86, 83), 42, 38);
        }
        else
        {
            AddAura(drawing, "#167FC3E1", new Point(5, 86), 31, 46);
            AddAura(drawing, "#148C9DD7", new Point(97, 68), 28, 45);
        }
        AddAura(drawing, "#18FFFFFF", new Point(66, 98), 54, 17);
        drawing.Freeze();

        var brush = new DrawingBrush(drawing)
        {
            Viewbox = new Rect(0, 0, 100, 100),
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
        };
        brush.Freeze();
        return brush;
    }

    private static void AddAura(DrawingGroup drawing, string value, Point center, double radiusX, double radiusY)
    {
        var color = Parse(value);
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        brush.GradientStops.Add(new GradientStop(color, 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
        drawing.Children.Add(new GeometryDrawing(brush, null, new EllipseGeometry(center, radiusX, radiusY)));
    }

    private static LinearGradientBrush Gradient(Point start, Point end, params (string Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var stop in stops)
        {
            brush.GradientStops.Add(new GradientStop(Parse(stop.Color), stop.Offset));
        }
        brush.Freeze();
        return brush;
    }

    private static Color Parse(string value) =>
        (Color)System.Windows.Media.ColorConverter.ConvertFromString(value);

}
