using Avalonia.Controls;

namespace Yita.Desktop;

/// <summary>
/// Small cross-platform tray/menu-bar shell for the Avalonia app. Platform
/// adapters still own native input capture; this class only exposes safe UI
/// actions and never reads selection text.
/// </summary>
internal sealed class YitaTrayController : IDisposable
{
    private readonly TrayIcon _tray;
    private readonly NativeMenuItem _enabledItem;
    private readonly Action _showSettings;
    private readonly Action _toggleEnabled;
    private readonly Action _exit;
    private bool _disposed;

    public YitaTrayController(
        Action showSettings,
        Action toggleEnabled,
        Action exit,
        bool isEnabled)
    {
        _showSettings = showSettings;
        _toggleEnabled = toggleEnabled;
        _exit = exit;
        _enabledItem = new NativeMenuItem { Header = "启用划词翻译", ToggleType = NativeMenuItemToggleType.CheckBox };
        _enabledItem.IsChecked = isEnabled;
        _enabledItem.Click += EnabledClick;

        var menu = new NativeMenu();
        var settings = new NativeMenuItem { Header = "打开设置" };
        settings.Click += SettingsClick;
        var exitItem = new NativeMenuItem { Header = "退出 Yita" };
        exitItem.Click += ExitClick;
        menu.Items.Add(settings);
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _tray = new TrayIcon
        {
            ToolTipText = "Yita · 译獭",
            Menu = menu,
            IsVisible = true,
        };
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Yita.ico");
        if (File.Exists(iconPath))
        {
            try { _tray.Icon = new WindowIcon(iconPath); }
            catch { /* Some desktop environments reject an icon format. */ }
        }
        _tray.Clicked += TrayClicked;
    }

    public void SetEnabled(bool enabled) => _enabledItem.IsChecked = enabled;

    private void TrayClicked(object? sender, EventArgs e) => _showSettings();

    private void SettingsClick(object? sender, EventArgs e) => _showSettings();

    private void EnabledClick(object? sender, EventArgs e) => _toggleEnabled();

    private void ExitClick(object? sender, EventArgs e) => _exit();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tray.Clicked -= TrayClicked;
        _enabledItem.Click -= EnabledClick;
        _tray.Dispose();
    }
}
