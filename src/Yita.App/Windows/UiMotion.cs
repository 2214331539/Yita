using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace Yita.Windows;

// Animate only composition properties so streaming text never reflows for an effect.
internal static class UiMotion
{
    internal static bool IsEnabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    public static readonly DependencyProperty PressFeedbackProperty = DependencyProperty.RegisterAttached(
        "PressFeedback", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, OnPressFeedbackChanged));

    public static void SetPressFeedback(DependencyObject target, bool value) => target.SetValue(PressFeedbackProperty, value);
    public static bool GetPressFeedback(DependencyObject target) => (bool)target.GetValue(PressFeedbackProperty);

    public static readonly DependencyProperty AnimateSwitchProperty = DependencyProperty.RegisterAttached(
        "AnimateSwitch", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, OnAnimateSwitchChanged));

    public static void SetAnimateSwitch(DependencyObject target, bool value) => target.SetValue(AnimateSwitchProperty, value);
    public static bool GetAnimateSwitch(DependencyObject target) => (bool)target.GetValue(AnimateSwitchProperty);

    internal static void Reveal(FrameworkElement element, bool scale = false)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        if (!IsEnabled) return;
        element.BeginAnimation(UIElement.OpacityProperty, Animation(0, 1, 180));
        if (scale)
        {
            var transform = new ScaleTransform(1, 1);
            element.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            element.RenderTransform = transform;
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, Animation(0.985, 1, 220));
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, Animation(0.985, 1, 220));
        }
    }

    private static DoubleAnimation Animation(double from, double to, double milliseconds) => new(
        from, to, TimeSpan.FromMilliseconds(milliseconds))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.Stop,
    };

    private static void OnPressFeedbackChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ButtonBase button) return;
        if ((bool)args.NewValue)
        {
            button.PreviewMouseLeftButtonDown += Press;
            button.PreviewMouseLeftButtonUp += Release;
            button.LostMouseCapture += Release;
            button.MouseLeave += Release;
        }
        else
        {
            button.PreviewMouseLeftButtonDown -= Press;
            button.PreviewMouseLeftButtonUp -= Release;
            button.LostMouseCapture -= Release;
            button.MouseLeave -= Release;
        }
    }

    private static void Press(object sender, System.Windows.Input.MouseButtonEventArgs args) => Scale((ButtonBase)sender, 0.96);
    private static void Release(object sender, System.Windows.Input.MouseEventArgs args) => Scale((ButtonBase)sender, 1);

    private static void Scale(ButtonBase button, double value)
    {
        if (!IsEnabled) value = 1;
        if (button.RenderTransform is not ScaleTransform transform)
        {
            transform = new ScaleTransform(1, 1);
            button.RenderTransform = transform;
            button.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        }
        var previous = transform.ScaleX;
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        transform.ScaleX = transform.ScaleY = value;
        if (!IsEnabled) return;
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, Animation(previous, value, 140));
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, Animation(previous, value, 140));
    }

    private static void OnAnimateSwitchChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not System.Windows.Controls.CheckBox checkBox) return;
        if ((bool)args.NewValue)
        {
            checkBox.Loaded += SwitchLoaded;
            checkBox.Checked += SwitchChanged;
            checkBox.Unchecked += SwitchChanged;
        }
        else
        {
            checkBox.Loaded -= SwitchLoaded;
            checkBox.Checked -= SwitchChanged;
            checkBox.Unchecked -= SwitchChanged;
        }
    }

    private static void SwitchLoaded(object sender, RoutedEventArgs args) => MoveSwitch((System.Windows.Controls.CheckBox)sender, false);
    private static void SwitchChanged(object sender, RoutedEventArgs args) => MoveSwitch((System.Windows.Controls.CheckBox)sender, true);

    private static void MoveSwitch(System.Windows.Controls.CheckBox checkBox, bool animate)
    {
        checkBox.ApplyTemplate();
        if (checkBox.Template.FindName("Knob", checkBox) is not FrameworkElement knob) return;
        if (knob.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            knob.RenderTransform = transform;
        }
        var previous = transform.X;
        var destination = checkBox.IsChecked == true ? 18 : 0;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = destination;
        if (animate && checkBox.IsLoaded && IsEnabled)
            transform.BeginAnimation(TranslateTransform.XProperty, Animation(previous, destination, 200));
    }
}
