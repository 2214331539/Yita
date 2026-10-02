using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using System.Globalization;

namespace Yita.Desktop;

public sealed partial class WindowSizePresetBar : UserControl
{
    public WindowSizePresetBar() => InitializeComponent();
    internal event EventHandler<string>? PresetSelected;
    internal event EventHandler<double>? TextSizeChanged;
    internal double TextSize
    {
        get => FontSizeSlider.Value;
        set => FontSizeSlider.Value = Math.Clamp(double.IsFinite(value) ? value : 16.5, FontSizeSlider.Minimum, FontSizeSlider.Maximum);
    }
    internal void ConfigureTextSize(double minimum, double maximum, double initial)
    {
        FontSizeSlider.Minimum = minimum;
        FontSizeSlider.Maximum = maximum;
        TextSize = initial;
    }
    internal void ApplyUiLanguage(string language)
    {
        var labels = language == "zh-CN"
            ? new[] { "小窗口", "中窗口", "大窗口", "横向长方形", "竖向长方形", "正方形" }
            : new[] { "Small window", "Medium window", "Large window", "Wide rectangle", "Tall rectangle", "Square window" };
        var buttons = new[] { SmallButton, MediumButton, LargeButton, WideButton, TallButton, SquareButton };
        for (var i = 0; i < buttons.Length; i++) ToolTip.SetTip(buttons[i], labels[i]);
        ToolTip.SetTip(FontSizeSlider, language == "zh-CN" ? "拖动调节字号" : "Drag to adjust text size");
    }
    private void PresetClick(object? sender, RoutedEventArgs e)
    { if ((sender as Button)?.Tag is string preset) PresetSelected?.Invoke(this, preset); }
    private void FontSizeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (FontSizeValueText is not null) FontSizeValueText.Text = e.NewValue.ToString("0.#", CultureInfo.InvariantCulture);
        TextSizeChanged?.Invoke(this, e.NewValue);
    }
    internal static void ApplyConversationPreset(Window window, string preset)
    {
        var requested = preset switch
        {
            "Small" => new Size(360, 260), "Large" => new Size(760, 560), "Wide" => new Size(780, 340),
            "Tall" => new Size(420, 640), "Square" => new Size(520, 520), _ => new Size(520, 360),
        };
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        var maximum = screen is null ? new Size(window.MaxWidth, window.MaxHeight)
            : new Size(Math.Min(window.MaxWidth, (screen.WorkingArea.Width - 16) / screen.Scaling),
                Math.Min(window.MaxHeight, (screen.WorkingArea.Height - 16) / screen.Scaling));
        window.Width = Math.Clamp(requested.Width, Math.Min(window.MinWidth, maximum.Width), maximum.Width);
        window.Height = Math.Clamp(requested.Height, Math.Min(window.MinHeight, maximum.Height), maximum.Height);
        Constrain(window);
    }
    internal static void Constrain(Window window)
    {
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        window.Position = new PixelPoint(Math.Clamp(window.Position.X, area.X + 8,
            Math.Max(area.X + 8, area.Right - (int)Math.Ceiling(window.Width * screen.Scaling) - 8)),
            Math.Clamp(window.Position.Y, area.Y + 8,
                Math.Max(area.Y + 8, area.Bottom - (int)Math.Ceiling(window.Height * screen.Scaling) - 8)));
    }
}
