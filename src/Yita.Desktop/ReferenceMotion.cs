using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Yita.Desktop;

internal static class ReferenceMotion
{
    private static readonly Dictionary<Control, RevealOperation> Active = new();
    private static bool _enabled = true;
    private static bool _reduceMotion;
    private static bool _systemAnimations = true;
    internal static bool Enabled
    {
        get => _enabled;
        set { _enabled = value; if (!CanAnimate) CancelAll(); }
    }
    internal static bool CanAnimate => Enabled && !_reduceMotion && _systemAnimations;
    internal static int ActiveCount => Active.Count;
    internal static bool SystemAnimationsEnabled => _systemAnimations;
    internal static void SetReduceMotion(bool value) => SetPreferences(value, _systemAnimations);
    internal static void SetSystemAnimations(bool value) => SetPreferences(_reduceMotion, value);

    internal static void SetPreferences(bool reduceMotion, bool systemAnimations)
    {
        _reduceMotion = reduceMotion;
        _systemAnimations = systemAnimations;
        if (!CanAnimate) CancelAll();
    }

    private static void CancelAll()
    {
        foreach (var operation in Active.Values.ToArray()) operation.Cancel();
    }

    internal static void Reveal(Control control, bool scale = false)
    {
        if (!CanAnimate || !control.IsAttachedToVisualTree()) return;
        var opacity = control.Opacity;
        if (Active.TryGetValue(control, out var previous)) { opacity = previous.OriginalOpacity; previous.Cancel(); }
        var operation = new RevealOperation(control, scale, opacity);
        Active[control] = operation;
        _ = RunAsync(operation);
    }

    private static async Task RunAsync(RevealOperation operation)
    {
        var control = operation.Control;
        try
        {
            var animation = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(180), Easing = new CubicEaseOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, operation.OriginalOpacity) } },
                },
            };
            var fade = animation.RunAsync(control, operation.Token);
            if (operation.Transform is null) { await fade; return; }
            var size = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ScaleTransform.ScaleXProperty, .985), new Setter(ScaleTransform.ScaleYProperty, .985) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ScaleTransform.ScaleXProperty, 1d), new Setter(ScaleTransform.ScaleYProperty, 1d) } },
                },
            };
            await Task.WhenAll(fade, size.RunAsync(control, operation.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { System.Diagnostics.Trace.TraceError("Reveal animation failed: {0}", exception.GetType().Name); }
        finally
        {
            if (Active.TryGetValue(control, out var current) && ReferenceEquals(current, operation)) Active.Remove(control);
            operation.Dispose();
        }
    }

    private sealed class RevealOperation : IDisposable
    {
        private readonly CancellationTokenSource _cancel = new();
        private readonly ITransform? _originalTransform;
        private readonly RelativePoint _originalOrigin;
        private bool _restored;
        internal Control Control { get; }
        internal double OriginalOpacity { get; }
        internal ScaleTransform? Transform { get; }
        internal CancellationToken Token => _cancel.Token;
        internal RevealOperation(Control control, bool scale, double opacity)
        {
            Control = control;
            OriginalOpacity = opacity;
            _originalTransform = control.RenderTransform;
            _originalOrigin = control.RenderTransformOrigin;
            if (scale)
            {
                Transform = new ScaleTransform(1, 1);
                control.RenderTransformOrigin = RelativePoint.Center;
                control.RenderTransform = Transform;
            }
            control.DetachedFromVisualTree += Detached;
        }
        private void Detached(object? sender, VisualTreeAttachmentEventArgs args) => Cancel();
        internal void Cancel() { _cancel.Cancel(); Restore(); }
        private void Restore()
        {
            if (_restored) return;
            _restored = true;
            Control.Opacity = OriginalOpacity;
            if (Transform is not null && ReferenceEquals(Control.RenderTransform, Transform))
            {
                Control.RenderTransform = _originalTransform;
                Control.RenderTransformOrigin = _originalOrigin;
            }
        }
        public void Dispose()
        {
            Control.DetachedFromVisualTree -= Detached;
            Restore();
            _cancel.Dispose();
        }
    }
}
