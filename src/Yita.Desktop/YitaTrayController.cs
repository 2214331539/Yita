using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Yita.Core.Platform;
using Yita.Core.Selection;

namespace Yita.Desktop;

internal sealed class YitaTrayController : IDisposable
{
    private readonly TrayIcon? _tray;
    private readonly IStatusIcon? _statusIcon;
    private readonly Action _showSettings;
    private readonly Action _toggleEnabled;
    private readonly Action _exit;
    private readonly ISelectionRuntime? _runtime;
    private readonly Action _translateClipboard;
    private readonly Action _diagnostics;
    private readonly Action _about;
    private readonly NativeMenuItem? _enabledItem;
    private readonly NativeMenuItem? _settingsItem;
    private readonly NativeMenuItem? _exitItem;
    private readonly NativeMenuItem? _clipboardItem;
    private readonly NativeMenuItem? _diagnosticsItem;
    private readonly NativeMenuItem? _aboutItem;
    private Window? _menu;
    private ScreenPoint _menuAnchor;
    private bool _enabled;
    private bool _chinese;
    private bool _disposed;
    internal Window? MenuWindow => _menu;

    internal YitaTrayController(Action showSettings, Action toggleEnabled, Action exit, bool isEnabled,
        ISelectionRuntime? runtime = null, Action? diagnostics = null, Action? about = null, bool createIcon = true,
        Action? translateClipboard = null, Func<string, IStatusIcon?>? createStatusIcon = null)
    {
        (_showSettings, _toggleEnabled, _exit, _enabled, _runtime) = (showSettings, toggleEnabled, exit, isEnabled, runtime);
        _diagnostics = diagnostics ?? (() => { });
        _about = about ?? (() => { });
        _translateClipboard = translateClipboard ?? (() => _runtime?.TranslateClipboard());
        if (!createIcon) return;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Yita.ico");
        _statusIcon = (createStatusIcon ?? new DesktopPlatformServices().CreateStatusIcon)(iconPath);
        if (_statusIcon is not null)
        {
            _statusIcon.MenuRequested += OnMenuRequested;
            _statusIcon.OpenRequested += OnOpenRequested;
            return;
        }
        var menu = new NativeMenu();
        _clipboardItem = new NativeMenuItem("Translate clipboard"); _clipboardItem.Click += (_, _) => _translateClipboard(); menu.Items.Add(_clipboardItem);
        _settingsItem = new NativeMenuItem("Settings…"); _settingsItem.Click += (_, _) => _showSettings(); menu.Items.Add(_settingsItem);
        _enabledItem = new NativeMenuItem("Enable selection translation") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = isEnabled, IsEnabled = _runtime is not null };
        _enabledItem.Click += (_, _) => _toggleEnabled(); menu.Items.Add(_enabledItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        _diagnosticsItem = new NativeMenuItem("Copy performance diagnostics"); _diagnosticsItem.Click += (_, _) => _diagnostics(); menu.Items.Add(_diagnosticsItem);
        _aboutItem = new NativeMenuItem("About Yita"); _aboutItem.Click += (_, _) => _about(); menu.Items.Add(_aboutItem);
        _exitItem = new NativeMenuItem("Exit"); _exitItem.Click += (_, _) => _exit(); menu.Items.Add(_exitItem);
        _tray = new TrayIcon { ToolTipText = "Yita", Menu = menu, IsVisible = true };
        if (File.Exists(iconPath)) _tray.Icon = new WindowIcon(iconPath);
        _tray.Clicked += (_, _) => _showSettings();
    }

    private void OnMenuRequested(object? sender, ScreenPoint point) => Dispatcher.UIThread.Post(() => ShowMenu(point));
    private void OnOpenRequested(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() => { if (!_disposed) _showSettings(); });

    internal void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (_enabledItem is not null) _enabledItem.IsChecked = enabled;
        RefreshMenu();
    }
    internal void ApplyUiLanguage(string language)
    {
        _chinese = language == "zh-CN";
        if (_settingsItem is not null) _settingsItem.Header = L("Settings…", "设置…");
        if (_enabledItem is not null) _enabledItem.Header = L("Enable selection translation", "启用划词翻译");
        if (_exitItem is not null) _exitItem.Header = L("Exit", "退出");
        if (_clipboardItem is not null) _clipboardItem.Header = L("Translate clipboard", "翻译剪贴板");
        if (_diagnosticsItem is not null) _diagnosticsItem.Header = L("Copy performance diagnostics", "复制性能诊断");
        if (_aboutItem is not null) _aboutItem.Header = L("About Yita", "关于 Yita");
        RefreshMenu();
    }

    private void RefreshMenu()
    {
        if (_menu is not { IsVisible: true } window) return;
        window.Content = CreateMenuContent();
        SizeAndPlaceMenu(window, _menuAnchor);
    }

    internal void ShowMenu(ScreenPoint point)
    {
        if (_disposed || !point.IsFinite) return;
        _menu?.Close();
        _menuAnchor = point;
        var window = new Window
        {
            SystemDecorations = SystemDecorations.None, ShowInTaskbar = false, Topmost = true, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.Manual, FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 40d / 3,
            Background = ReferenceTheme.Brush("#FFFCF7"), Foreground = ReferenceTheme.Brush("#302D29"),
        };
        RenderOptions.SetTextRenderingMode(window, TextRenderingMode.Antialias);
        window.Content = CreateMenuContent();
        _menu = window;
        // Start on the clicked monitor so the first native layout uses its DPI.
        window.Position = new PixelPoint((int)point.X, (int)point.Y);
        SizeAndPlaceMenu(window, point);
        window.Deactivated += (_, _) => window.Close();
        window.Closed += (_, _) => { if (ReferenceEquals(_menu, window)) _menu = null; };
        window.Opened += (_, _) => SizeAndPlaceMenu(window, point);
        window.KeyDown += (_, args) => { if (args.Key == Avalonia.Input.Key.Escape) window.Close(); };
        window.Show();
        window.Activate();
    }

    private Border CreateMenuContent()
    {
        var stack = new StackPanel { Margin = new Thickness(4), MinWidth = 260 };
        stack.Children.Add(new TextBlock { Text = "Yita · " + L(_enabled ? "Enabled" : "Paused", _enabled ? "已启用" : "已暂停"),
            Margin = new Thickness(24, 6, 12, 6), FontWeight = FontWeight.Bold });
        Separator(stack);
        Command(stack, L("Translate clipboard (Ctrl+Shift+T)", "翻译剪贴板（Ctrl+Shift+T）"), _translateClipboard);
        Command(stack, L("Enable selection translation", "启用划词翻译"), _toggleEnabled, _enabled);
        Separator(stack);
        Command(stack, L("Settings…", "设置…"), _showSettings);
        Command(stack, L("Repair input capture", "修复划词捕获"), () => _ = Task.Run(() => _runtime?.RepairInputCapture()));
        Command(stack, L("Copy performance diagnostics", "复制性能诊断"), _diagnostics);
        Command(stack, L("About Yita", "关于 Yita"), _about);
        Separator(stack);
        Command(stack, L("Exit", "退出"), _exit);
        return new Border { BorderBrush = ReferenceTheme.Brush("#C7BDAE"), BorderThickness = new Thickness(1), Child = stack };
    }

    private static void SizeAndPlaceMenu(Window window, ScreenPoint point)
    {
        if (window.Content is not Control content) return;
        var screen = window.Screens.ScreenFromPoint(new PixelPoint((int)point.X, (int)point.Y)) ?? window.Screens.Primary;
        if (screen is null) return;
        content.Measure(new Size(screen.WorkingArea.Width / screen.Scaling, screen.WorkingArea.Height / screen.Scaling));
        window.Width = content.DesiredSize.Width;
        window.Height = content.DesiredSize.Height;
        window.Position = ResolveMenuPosition(point, content.DesiredSize, screen.WorkingArea, screen.Scaling);
    }

    internal static PixelPoint ResolveMenuPosition(ScreenPoint point, Size logicalSize, PixelRect area, double scale)
    {
        var width = (int)Math.Ceiling(logicalSize.Width * scale);
        var height = (int)Math.Ceiling(logicalSize.Height * scale);
        var gap = (int)Math.Ceiling(8 * scale);
        var x = point.X > area.X + area.Width / 2d ? point.X - width + gap : point.X - gap;
        var y = point.Y > area.Y + area.Height / 2d ? point.Y - height - gap : point.Y + gap;
        return new PixelPoint((int)Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width)),
            (int)Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height)));
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
        if (_statusIcon is not null)
        {
            _statusIcon.MenuRequested -= OnMenuRequested;
            _statusIcon.OpenRequested -= OnOpenRequested;
            _statusIcon.Dispose();
        }
        _tray?.Dispose();
    }
}
