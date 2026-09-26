using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Yita.Settings;
using Forms = System.Windows.Forms;

namespace Yita.Windows;

internal enum WindowSizePreset
{
    Small,
    Medium,
    Large,
    Wide,
    Tall,
    Square,
}

internal sealed class WindowSizePresetSelectedEventArgs(WindowSizePreset preset) : EventArgs
{
    internal WindowSizePreset Preset { get; } = preset;
}

internal sealed class WindowFontSizeChangedEventArgs(double oldValue, double newValue) : EventArgs
{
    internal double OldValue { get; } = oldValue;

    internal double NewValue { get; } = newValue;
}

internal readonly record struct WindowPresetDimensions(double Width, double Height);

internal static class WindowSizePresetCatalog
{
    internal static WindowPresetDimensions ForTranslation(WindowSizePreset preset) => preset switch
    {
        WindowSizePreset.Small => new(740, 300),
        WindowSizePreset.Medium => new(860, 500),
        WindowSizePreset.Large => new(1000, 700),
        WindowSizePreset.Wide => new(1080, 420),
        WindowSizePreset.Tall => new(740, 820),
        WindowSizePreset.Square => new(760, 760),
        _ => new(860, 500),
    };

    internal static WindowPresetDimensions ForConversation(WindowSizePreset preset) => preset switch
    {
        WindowSizePreset.Small => new(360, 260),
        WindowSizePreset.Medium => new(520, 360),
        WindowSizePreset.Large => new(760, 560),
        WindowSizePreset.Wide => new(780, 340),
        WindowSizePreset.Tall => new(420, 640),
        WindowSizePreset.Square => new(520, 520),
        _ => new(520, 360),
    };

    internal static void Apply(Window window, WindowPresetDimensions requested)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var screen = Forms.Screen.FromHandle(handle);
        var dpi = VisualTreeHelper.GetDpi(window);
        var screenMaximumWidth = Math.Max(1, (screen.WorkingArea.Width - 16) / dpi.DpiScaleX);
        var screenMaximumHeight = Math.Max(1, (screen.WorkingArea.Height - 16) / dpi.DpiScaleY);
        var maximumWidth = double.IsFinite(window.MaxWidth)
            ? Math.Min(window.MaxWidth, screenMaximumWidth)
            : screenMaximumWidth;
        var maximumHeight = double.IsFinite(window.MaxHeight)
            ? Math.Min(window.MaxHeight, screenMaximumHeight)
            : screenMaximumHeight;

        window.Width = Math.Clamp(requested.Width, window.MinWidth, Math.Max(window.MinWidth, maximumWidth));
        window.Height = Math.Clamp(requested.Height, window.MinHeight, Math.Max(window.MinHeight, maximumHeight));
        window.UpdateLayout();

        const double margin = 8;
        var minimumLeft = (screen.WorkingArea.Left / dpi.DpiScaleX) + margin;
        var minimumTop = (screen.WorkingArea.Top / dpi.DpiScaleY) + margin;
        var maximumLeft = (screen.WorkingArea.Right / dpi.DpiScaleX) - window.ActualWidth - margin;
        var maximumTop = (screen.WorkingArea.Bottom / dpi.DpiScaleY) - window.ActualHeight - margin;
        var currentLeft = double.IsFinite(window.Left) ? window.Left : minimumLeft;
        var currentTop = double.IsFinite(window.Top) ? window.Top : minimumTop;
        window.Left = Math.Clamp(currentLeft, minimumLeft, Math.Max(minimumLeft, maximumLeft));
        window.Top = Math.Clamp(currentTop, minimumTop, Math.Max(minimumTop, maximumTop));
    }
}

public partial class WindowSizePresetBar : System.Windows.Controls.UserControl
{
    public WindowSizePresetBar()
    {
        InitializeComponent();
        ApplyUiLanguage(UiLanguageCatalog.DefaultLanguageId);
    }

    internal event EventHandler<WindowSizePresetSelectedEventArgs>? PresetSelected;

    internal event EventHandler<WindowFontSizeChangedEventArgs>? FontSizeChanged;

    internal double FontSizeValue
    {
        get => FontSizeSlider.Value;
        set
        {
            var safeValue = double.IsFinite(value) ? value : 16.5;
            FontSizeSlider.Value = Math.Clamp(
                safeValue,
                FontSizeSlider.Minimum,
                FontSizeSlider.Maximum);
        }
    }

    internal double MinimumFontSize => FontSizeSlider.Minimum;

    internal double MaximumFontSize => FontSizeSlider.Maximum;

    internal void ConfigureFontSize(double minimum, double maximum, double value)
    {
        if (!double.IsFinite(minimum)
            || minimum <= 0
            || !double.IsFinite(maximum)
            || maximum <= minimum)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        FontSizeSlider.Minimum = minimum;
        FontSizeSlider.Maximum = maximum;
        FontSizeValue = value;
    }

    internal void ApplyUiLanguage(string? uiLanguage)
    {
        var isChinese = UiLanguageCatalog.Normalize(uiLanguage)
            == UiLanguageCatalog.SimplifiedChineseLanguageId;
        ApplyLabel(SmallButton, isChinese ? "小窗口" : "Small window");
        ApplyLabel(MediumButton, isChinese ? "中窗口" : "Medium window");
        ApplyLabel(LargeButton, isChinese ? "大窗口" : "Large window");
        ApplyLabel(WideButton, isChinese ? "横向长方形" : "Wide rectangle");
        ApplyLabel(TallButton, isChinese ? "竖向长方形" : "Tall rectangle");
        ApplyLabel(SquareButton, isChinese ? "正方形" : "Square window");
        var fontSizeLabel = isChinese ? "拖动调节字号" : "Drag to adjust text size";
        FontSizePanel.ToolTip = fontSizeLabel;
        System.Windows.Automation.AutomationProperties.SetName(FontSizeSlider, fontSizeLabel);
    }

    internal bool HasDirectFontSizeSliderForVisualTest()
    {
        return FontSizeSlider.Visibility == Visibility.Visible
               && FontSizeSlider.IsEnabled
               && FontSizeSlider.ActualWidth >= 48;
    }

    internal bool HasSingleRowLayoutForVisualTest()
    {
        if (ActualHeight <= 0 || ActualHeight > 42)
        {
            return false;
        }

        var controls = new FrameworkElement[]
        {
            SmallButton,
            MediumButton,
            LargeButton,
            WideButton,
            TallButton,
            SquareButton,
            FontSizePanel,
        };
        var centers = controls
            .Select(control => control.TranslatePoint(
                new System.Windows.Point(control.ActualWidth / 2, control.ActualHeight / 2),
                this).Y)
            .ToArray();
        return centers.Max() - centers.Min() <= 2;
    }

    private void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string tag }
            && Enum.TryParse<WindowSizePreset>(tag, ignoreCase: true, out var preset))
        {
            PresetSelected?.Invoke(this, new WindowSizePresetSelectedEventArgs(preset));
        }
    }

    private void FontSizeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!double.IsFinite(e.NewValue) || e.NewValue <= 0)
        {
            return;
        }

        if (FontSizeValueText is not null)
        {
            FontSizeValueText.Text = e.NewValue.ToString(
                "0.#",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        FontSizeChanged?.Invoke(this, new WindowFontSizeChangedEventArgs(e.OldValue, e.NewValue));
    }

    private static void ApplyLabel(System.Windows.Controls.Button button, string label)
    {
        button.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(button, label);
    }
}
