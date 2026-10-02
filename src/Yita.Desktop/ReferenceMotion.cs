using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Yita.Native.Windows;

namespace Yita.Desktop;

internal static class ReferenceMotion
{
    internal static bool Enabled { get; set; } = true;
    internal static void Reveal(Control control, bool scale = false)
    {
        if (!Enabled || !WindowsDisplayPreferences.AnimationsEnabled || !control.IsAttachedToVisualTree()) return;
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(180), Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
            },
        };
        _ = animation.RunAsync(control);
        if (scale)
        {
            control.RenderTransformOrigin = RelativePoint.Center;
            var transform = new ScaleTransform(1, 1);
            control.RenderTransform = transform;
            var size = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ScaleTransform.ScaleXProperty, .985), new Setter(ScaleTransform.ScaleYProperty, .985) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ScaleTransform.ScaleXProperty, 1d), new Setter(ScaleTransform.ScaleYProperty, 1d) } },
                },
            };
            _ = size.RunAsync(control);
        }
    }
}
