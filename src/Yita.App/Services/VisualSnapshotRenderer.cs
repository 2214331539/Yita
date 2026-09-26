using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Yita.Services;

/// <summary>
/// Renders only an Yita window into a PNG for deterministic visual
/// review. It never captures the desktop or another application's pixels.
/// </summary>
internal static class VisualSnapshotRenderer
{
    internal static void SavePng(FrameworkElement element, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(element);

        SaveBitmap(bitmap, outputPath);
    }

    /// <summary>Composes our own window over a synthetic background, never the desktop.</summary>
    internal static void SaveOnBackdrop(FrameworkElement element, string outputPath, bool dark)
    {
        element.UpdateLayout();
        const double inset = 40;
        var size = new System.Windows.Size(element.ActualWidth + inset * 2, element.ActualHeight + inset * 2);
        var scene = new DrawingVisual();
        using (var context = scene.RenderOpen())
        {
            var background = new LinearGradientBrush(
                dark ? System.Windows.Media.Color.FromRgb(38, 53, 68) : System.Windows.Media.Color.FromRgb(186, 201, 211),
                dark ? System.Windows.Media.Color.FromRgb(19, 27, 37) : System.Windows.Media.Color.FromRgb(229, 235, 240),
                32);
            context.DrawRectangle(background, null, new Rect(size));
            var aura = new RadialGradientBrush(
                System.Windows.Media.Color.FromArgb(dark ? (byte)70 : (byte)95, 127, 184, 203),
                System.Windows.Media.Colors.Transparent);
            context.DrawEllipse(aura, null,
                new System.Windows.Point(size.Width * 0.12, size.Height * 0.86), size.Width * 0.52, size.Height * 0.94);
            // A structured, synthetic backdrop makes transparency unmistakable:
            // these shapes continue behind and outside the actual WPF window.
            context.PushTransform(new RotateTransform(-18, size.Width * 0.5, size.Height * 0.5));
            var coolBand = new SolidColorBrush(dark
                ? System.Windows.Media.Color.FromRgb(68, 104, 130)
                : System.Windows.Media.Color.FromRgb(145, 197, 213));
            var warmBand = new SolidColorBrush(dark
                ? System.Windows.Media.Color.FromRgb(108, 82, 137)
                : System.Windows.Media.Color.FromRgb(200, 168, 217));
            context.DrawRoundedRectangle(coolBand, null,
                new Rect(size.Width * 0.25, -size.Height, size.Width * 0.16, size.Height * 3), 32, 32);
            context.DrawRoundedRectangle(warmBand, null,
                new Rect(size.Width * 0.65, -size.Height, size.Width * 0.12, size.Height * 3), 32, 32);
            context.Pop();
            context.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null,
                new Rect(inset, inset, element.ActualWidth, element.ActualHeight));
        }
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(size.Width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(size.Height * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(scene);
        SaveBitmap(bitmap, outputPath);
    }

    private static void SaveBitmap(RenderTargetBitmap bitmap, string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("预览图片路径无效。");
        Directory.CreateDirectory(directory);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }
}
