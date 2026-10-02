using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Yita.Core.Selection;
using Yita.Native.Windows;

namespace Yita.Desktop;

internal sealed class YitaTrayController : IDisposable
{
    private readonly TrayIcon? _tray;
    private readonly WindowsTrayIcon? _windowsTray;
    private readonly Action _showSettings;
    private readonly Action _toggleEnabled;
    private readonly Action _exit;
    private readonly WindowsSelectionRuntime? _runtime;
    private readonly Action _diagnostics;
    private readonly Action _about;
    private readonly NativeMenuItem? _enabledItem;
    private Window? _menu;
    private bool _enabled;
    private bool _chinese;
    private bool _disposed;

    internal YitaTrayController(Action showSettings, Action toggleEnabled, Action exit, bool isEnabled,
        WindowsSelectionRuntime? runtime = null, Action? diagnostics = null, Action? about = null)
    {
        (_showSettings, _toggleEnabled, _exit, _enabled, _runtime) = (showSettings, toggleEnabled, exit, isEnabled, runtime);
        _diagnostics = diagnostics ?? (() => { });
        _about = about ?? (() => { });
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Yita.ico");
        if (OperatingSystem.IsWindows())
        {
            _windowsTray = new WindowsTrayIcon(iconPath);
            _windowsTray.MenuRequested += (_, point) => Dispatcher.UIThread.Post(() => ShowMenu(point));
            _windowsTray.OpenRequested += (_, _) => Dispatcher.UIThread.Post(() => { if (!_disposed) _showSettings(); });
            return;
        }
        var menu = new NativeMenu();
        var settings = new NativeMenuItem("Settings…"); settings.Click += (_, _) => _showSettings(); menu.Items.Add(settings);
        _enabledItem = new NativeMenuItem("Enable selection translation") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = isEnabled };
        _enabledItem.Click += (_, _) => _toggleEnabled(); menu.Items.Add(_enabledItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        var exitItem = new NativeMenuItem("Exit"); exitItem.Click += (_, _) => _exit(); menu.Items.Add(exitItem);
        _tray = new TrayIcon { ToolTipText = "Yita", Menu = menu, IsVisible = true };
        if (File.Exists(iconPath)) _tray.Icon = new WindowIcon(iconPath);
        _tray.Clicked += (_, _) => _showSettings();
    }

    internal void SetEnabled(bool enabled)
    { _enabled = enabled; if (_enabledItem is not null) _enabledItem.IsChecked = enabled; }
    internal void ApplyUiLanguage(string language) => _chinese = language == "zh-CN";

    private void ShowMenu(ScreenPoint point)
    {
        if (_disposed) return;
        _menu?.Close();
        var window = new Window
        {
            SystemDecorations = SystemDecorations.None, ShowInTaskbar = false, Topmost = true, CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight, FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 40d / 3,
            Background = ReferenceTheme.Brush("#FFFCF7"), Foreground = ReferenceTheme.Brush("#302D29"),
        };
        RenderOptions.SetTextRenderingMode(window, TextRenderingMode.Antialias);
        var stack = new StackPanel { Margin = new Thickness(4), MinWidth = 260 };
        stack.Children.Add(new TextBlock { Text = "Yita · " + L(_enabled ? "Enabled" : "Paused", _enabled ? "已启用" : "已暂停"),
            Margin = new Thickness(24, 6, 12, 6), FontWeight = FontWeight.Bold });
        Separator(stack);
        Command(stack, L("Translate clipboard (Ctrl+Shift+T)", "翻译剪贴板（Ctrl+Shift+T）"), () => _runtime?.TranslateClipboard());
        Command(stack, L("Enable selection translation", "启用划词翻译"), _toggleEnabled, _enabled);
        Separator(stack);
        Command(stack, L("Settings…", "设置…"), _showSettings);
        Command(stack, L("Repair input capture", "修复划词捕获"), () => _ = Task.Run(() => _runtime?.RepairInputCapture()));
        Command(stack, L("Copy performance diagnostics", "复制性能诊断"), _diagnostics);
        Command(stack, L("About Yita", "关于 Yita"), _about);
        Separator(stack);
        Command(stack, L("Exit", "退出"), _exit);
        window.Content = new Border { BorderBrush = ReferenceTheme.Brush("#C7BDAE"), BorderThickness = new Thickness(1), Child = stack };
        _menu = window;
        window.Deactivated += (_, _) => window.Close();
        window.Opened += (_, _) =>
        {
            var screen = window.Screens.ScreenFromPoint(new PixelPoint((int)point.X, (int)point.Y)) ?? window.Screens.Primary;
            if (screen is null) return;
            var bounds = screen.WorkingArea;
            window.Position = new PixelPoint(Math.Clamp((int)point.X, bounds.X, Math.Max(bounds.X, bounds.Right - (int)(window.Bounds.Width * screen.Scaling))),
                Math.Clamp((int)point.Y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - (int)(window.Bounds.Height * screen.Scaling))));
        };
        window.Show();
        window.Activate();
    }

    private void Command(StackPanel stack, string label, Action action, bool check = false)
    {
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*") };
        if (check) content.Children.Add(new TextBlock { Text = "\uE73E", FontFamily = new FontFamily("Segoe Fluent Icons"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var text = new TextBlock { Text = label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }; Grid.SetColumn(text, 1); content.Children.Add(text);
        var button = new Button { Content = content, Classes = { "tray-command" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        button.Click += (_, _) => { _menu?.Close(); if (!_disposed) action(); };
        stack.Children.Add(button);
    }
    private static void Separator(StackPanel stack) => stack.Children.Add(new Border
    { Height = 1, Margin = new Thickness(5, 4), Background = ReferenceTheme.Brush("#E3DCD0") });
    private string L(string english, string chinese) => _chinese ? chinese : english;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _menu?.Close();
        _windowsTray?.Dispose();
        _tray?.Dispose();
    }
}
