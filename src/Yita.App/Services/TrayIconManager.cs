using System.Drawing;
using System.Reflection;
using Forms = System.Windows.Forms;

namespace Yita.Services;

internal sealed class TrayIconManager : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _statusMenuItem;
    private readonly Forms.ToolStripMenuItem _enabledMenuItem;
    private readonly Forms.ToolStripMenuItem _translateClipboardMenuItem;
    private readonly Forms.ToolStripMenuItem _settingsMenuItem;
    private readonly Forms.ToolStripMenuItem _restartInputCaptureMenuItem;
    private readonly Forms.ToolStripMenuItem _diagnosticsMenuItem;
    private readonly Forms.ToolStripMenuItem _aboutMenuItem;
    private readonly Forms.ToolStripMenuItem _exitMenuItem;
    private readonly Icon _applicationIcon;
    private bool _synchronizingEnabledState;
    private string _uiLanguage;

    public TrayIconManager(bool isEnabled, string? uiLanguage = null)
    {
        _uiLanguage = NormalizeLanguage(uiLanguage);
        _enabledMenuItem = new Forms.ToolStripMenuItem
        {
            Checked = isEnabled,
            CheckOnClick = true,
        };
        _enabledMenuItem.CheckedChanged += (_, _) =>
        {
            if (!_synchronizingEnabledState)
            {
                UpdateStatus(_enabledMenuItem.Checked);
                EnabledChanged?.Invoke(_enabledMenuItem.Checked);
            }
        };

        _statusMenuItem = new Forms.ToolStripMenuItem
        {
            Enabled = false,
            Font = new Font(Forms.Control.DefaultFont, FontStyle.Bold),
        };
        _translateClipboardMenuItem = new Forms.ToolStripMenuItem();
        _translateClipboardMenuItem.Click += (_, _) => TranslateClipboardRequested?.Invoke();
        _settingsMenuItem = new Forms.ToolStripMenuItem();
        _settingsMenuItem.Click += (_, _) => SettingsRequested?.Invoke();
        _restartInputCaptureMenuItem = new Forms.ToolStripMenuItem();
        _restartInputCaptureMenuItem.Click += (_, _) => RestartInputCaptureRequested?.Invoke();
        _diagnosticsMenuItem = new Forms.ToolStripMenuItem();
        _diagnosticsMenuItem.Click += (_, _) => DiagnosticsRequested?.Invoke();
        _aboutMenuItem = new Forms.ToolStripMenuItem();
        _aboutMenuItem.Click += (_, _) => AboutRequested?.Invoke();
        _exitMenuItem = new Forms.ToolStripMenuItem();
        _exitMenuItem.Click += (_, _) => ExitRequested?.Invoke();

        var contextMenu = new Forms.ContextMenuStrip();
        contextMenu.Items.Add(_statusMenuItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add(_translateClipboardMenuItem);
        contextMenu.Items.Add(_enabledMenuItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add(_settingsMenuItem);
        contextMenu.Items.Add(_restartInputCaptureMenuItem);
        contextMenu.Items.Add(_diagnosticsMenuItem);
        contextMenu.Items.Add(_aboutMenuItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add(_exitMenuItem);

        _applicationIcon = LoadApplicationIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Yita",
            Icon = _applicationIcon,
            ContextMenuStrip = contextMenu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
        ApplyUiLanguage(_uiLanguage);
    }

    public event Action? SettingsRequested;

    public event Action? ExitRequested;

    public event Action<bool>? EnabledChanged;

    public event Action? TranslateClipboardRequested;

    public event Action? DiagnosticsRequested;

    public event Action? RestartInputCaptureRequested;

    public event Action? AboutRequested;

    public void ApplyUiLanguage(string? uiLanguage)
    {
        _uiLanguage = NormalizeLanguage(uiLanguage);
        _enabledMenuItem.Text = L("Enable selection translation", "启用划词翻译");
        _translateClipboardMenuItem.Text = L("Translate clipboard (Ctrl+Shift+T)", "翻译剪贴板（Ctrl+Shift+T）");
        _settingsMenuItem.Text = L("Settings…", "设置…");
        _restartInputCaptureMenuItem.Text = L("Repair input capture", "修复划词捕获");
        _diagnosticsMenuItem.Text = L("Copy performance diagnostics", "复制性能诊断");
        _aboutMenuItem.Text = L("About Yita", "关于 Yita");
        _exitMenuItem.Text = L("Exit", "退出");
        UpdateStatus(_enabledMenuItem.Checked);
    }

    public void SetEnabled(bool value)
    {
        if (_enabledMenuItem.Checked == value)
        {
            UpdateStatus(value);
            return;
        }

        _synchronizingEnabledState = true;
        try
        {
            _enabledMenuItem.Checked = value;
        }
        finally
        {
            _synchronizingEnabledState = false;
        }

        UpdateStatus(value);
    }

    public void ShowInfo(string title, string message)
    {
        _notifyIcon.ShowBalloonTip(3500, title, LimitMessage(message), Forms.ToolTipIcon.Info);
    }

    public void ShowError(string message)
    {
        var safeMessage = string.IsNullOrWhiteSpace(message)
            ? L("Translation failed. Check your DeepSeek settings.", "翻译请求失败，请检查 DeepSeek 设置。")
            : message;
        _notifyIcon.ShowBalloonTip(
            5000,
            L("Yita translation failed", "Yita 翻译失败"),
            LimitMessage(safeMessage),
            Forms.ToolTipIcon.Error);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _applicationIcon.Dispose();
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/AppLogo.ico", UriKind.Absolute));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                using var source = new Icon(stream);
                return (Icon)source.Clone();
            }
        }
        catch (Exception)
        {
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private void UpdateStatus(bool isEnabled)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var displayVersion = $"{version?.Major ?? 0}.{version?.Minor ?? 0}";
        var state = isEnabled ? L("Enabled", "已启用") : L("Paused", "已暂停");
        _statusMenuItem.Text = $"Yita {displayVersion} · {state}";
        _notifyIcon.Text = $"Yita · {state}";
    }

    private string L(string english, string chinese) => _uiLanguage == "zh-CN" ? chinese : english;

    private static string NormalizeLanguage(string? language) =>
        string.Equals(language, "zh-CN", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";

    private static string LimitMessage(string? message)
    {
        var value = string.IsNullOrWhiteSpace(message) ? "Yita" : message.Trim();
        return value.Length <= 240 ? value : value[..240] + "…";
    }
}
