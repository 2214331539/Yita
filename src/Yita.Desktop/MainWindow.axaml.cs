using System.Net.Http;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Yita.Core.Placement;
using Yita.Core.Settings;
using Yita.Core.Selection;
using Yita.Core.Translation;
using Yita.Native.Windows;

namespace Yita.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly JsonSettingsStore _settingsStore = new();
    private readonly ISecretStore _secretStore = SecretStoreFactory.CreateDefault();
    private readonly WindowsSelectionRuntime? _windowsRuntime;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };
    private CancellationTokenSource? _translationCancellation;
    private CancellationTokenSource? _popupCancellation;
    private TranslationPopupWindow? _popup;
    private YitaSettings _settings = YitaSettings.Default;
    private TextBox _apiKeyField = null!;

    public bool IsSelectionTranslationEnabled => _settings.IsEnabled;
    public event EventHandler? SettingsChanged;

    public MainWindow() : this(null) { }

    public MainWindow(WindowsSelectionRuntime? windowsRuntime = null)
    {
        _windowsRuntime = windowsRuntime;
        InitializeComponent();
        _apiKeyField = this.FindControl<TextBox>("ApiKeyField")
            ?? throw new InvalidOperationException("API key field is missing from the desktop view.");
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        _settings = await _settingsStore.LoadAsync();
        _windowsRuntime?.Configure(
            _settings.IsEnabled,
            _settings.UseClipboardFallback,
            _settings.SelectionDelayMilliseconds);
        string? apiKey;
        try
        {
            apiKey = await _secretStore.ReadApiKeyAsync();
        }
        catch (Exception exception)
        {
            apiKey = null;
            SetStatus(ModelStatus, $"无法读取系统凭据：{exception.Message}", true);
        }
        EnabledCheckBox.IsChecked = _settings.IsEnabled;
        StartCheckBox.IsChecked = _settings.StartWithSystem;
        ClipboardCheckBox.IsChecked = _settings.UseClipboardFallback;
        HistoryCheckBox.IsChecked = _settings.AiHistoryEnabled;
        EndpointBox.Text = _settings.DeepSeekEndpoint;
        ModelBox.Text = _settings.DeepSeekModel;
        _apiKeyField.Text = apiKey ?? string.Empty;
        if (OperatingSystem.IsWindows())
        {
            try { WindowsStartupRegistration.Apply(_settings.StartWithSystem); }
            catch (Exception exception) { SetStatus(GeneralStatus, $"开机启动设置同步失败：{exception.Message}", true); }
        }
        TargetLanguageBox.SelectedIndex = _settings.TargetLanguage switch
        {
            "English" => 1,
            "日本語" => 2,
            _ => 0,
        };
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        SetStatus(GeneralStatus, "设置已从 Core 加载。", false);
    }

    private void NavigateClick(object? sender, RoutedEventArgs e)
    {
        var page = (sender as Button)?.Tag?.ToString() ?? "General";
        GeneralPage.IsVisible = page == "General";
        HistoryPage.IsVisible = page == "History";
        AppearancePage.IsVisible = page == "Appearance";
        ModelPage.IsVisible = page == "Model";
        (PageTitle.Text, PageDescription.Text) = page switch
        {
            "History" => ("AI 记录", "查看和控制翻译、解释与问答记录。"),
            "Appearance" => ("翻译与外观", "选择目标语言，并在接入原生取词前测试 Core 翻译流。"),
            "Model" => ("模型配置", "配置 OpenAI-compatible Endpoint、模型和 API Key。"),
            _ => ("常规", "控制 Yita 的运行状态和跨平台取词入口。"),
        };
    }

    private async void SaveSettingsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            _settings = _settings with
            {
                IsEnabled = EnabledCheckBox.IsChecked == true,
                StartWithSystem = StartCheckBox.IsChecked == true,
                UseClipboardFallback = ClipboardCheckBox.IsChecked == true,
                AiHistoryEnabled = HistoryCheckBox.IsChecked == true,
                TargetLanguage = SelectedTargetLanguage(),
                DeepSeekEndpoint = (EndpointBox.Text ?? string.Empty).Trim(),
                DeepSeekModel = (ModelBox.Text ?? string.Empty).Trim(),
            };
            await _settingsStore.SaveAsync(_settings);
            await _secretStore.SaveApiKeyAsync((_apiKeyField.Text ?? string.Empty).Trim());
            _windowsRuntime?.Configure(
                _settings.IsEnabled,
                _settings.UseClipboardFallback,
                _settings.SelectionDelayMilliseconds);
            if (OperatingSystem.IsWindows())
                WindowsStartupRegistration.Apply(_settings.StartWithSystem);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            var storage = _secretStore is WindowsCredentialSecretStore
                ? "Windows 安全凭据存储"
                : "当前进程内存（平台凭据适配器尚未接入）";
            SetStatus(ModelStatus, $"设置已保存，API Key 使用{storage}。", false);
        }
        catch (Exception exception)
        {
            SetStatus(ModelStatus, $"设置保存失败：{exception.Message}", true);
        }
    }

    internal void ToggleEnabledFromTray()
    {
        _settings = _settings with { IsEnabled = !_settings.IsEnabled };
        _windowsRuntime?.Configure(
            _settings.IsEnabled,
            _settings.UseClipboardFallback,
            _settings.SelectionDelayMilliseconds);
        EnabledCheckBox.IsChecked = _settings.IsEnabled;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        _ = PersistSettingsAsync(_settings);
    }

    private async void TestConnectionClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var translator = CreateTranslator();
            SetStatus(ModelStatus, "正在请求 DeepSeek…", false);
            await foreach (var _ in translator.TranslateAsync(new TranslationRequest("hello")))
            {
                SetStatus(ModelStatus, "DeepSeek 连接成功。", false);
                break;
            }
        }
        catch (Exception exception)
        {
            SetStatus(ModelStatus, exception.Message, true);
        }
    }

    private async void TranslateClick(object? sender, RoutedEventArgs e)
    {
        _translationCancellation?.Cancel();
        _translationCancellation?.Dispose();
        _translationCancellation = new CancellationTokenSource();
        try
        {
            var source = SourceTextBox.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source))
            {
                SetStatus(TranslationStatus, "请先输入待翻译文本。", true);
                return;
            }
            var coordinator = new TranslationCoordinator(CreateTranslator(), new MemoryTranslationCache());
            var output = new StringBuilder();
            TranslationText.Text = string.Empty;
            SetStatus(TranslationStatus, "正在流式翻译…", false);
            await foreach (var chunk in coordinator.TranslateAsync(
                               new TranslationRequest(source, TargetLanguage: SelectedTargetLanguage()),
                               _translationCancellation.Token))
            {
                output.Append(chunk.TextDelta);
                TranslationText.Text = output.ToString();
            }
            SetStatus(TranslationStatus, "翻译完成。", false);
        }
        catch (OperationCanceledException)
        {
            SetStatus(TranslationStatus, "翻译已取消。", false);
        }
        catch (Exception exception)
        {
            SetStatus(TranslationStatus, exception.Message, true);
        }
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => _translationCancellation?.Cancel();

    internal async Task ShowSelectionTranslationAsync(SelectionRequest request, SelectionResult result)
    {
        if (!_settings.IsEnabled) return;

        _popupCancellation?.Cancel();
        _popupCancellation?.Dispose();
        _popupCancellation = new CancellationTokenSource();
        if (_popup is null)
        {
            _popup = new TranslationPopupWindow();
            _popup.Moved += PopupMoved;
            _popup.Dismissed += (_, _) => _popupCancellation?.Cancel();
        }
        _popup.PlaceNear(request, result, SavedPopupOffset());
        if (!_popup.IsVisible) _popup.Show();
        if (!result.Succeeded)
        {
            _popup.SetError(result.Failure switch
            {
                SelectionFailureKind.PermissionDenied => "需要开启系统辅助功能权限后才能读取选区。",
                SelectionFailureKind.Empty => "没有读取到选中文字，请保持选区后重试。",
                SelectionFailureKind.UnsupportedApplication => "当前应用不允许通过快捷键读取选区。",
                _ => "无法读取当前选区。",
            });
            return;
        }

        try
        {
            var coordinator = new TranslationCoordinator(CreateTranslator(), new MemoryTranslationCache());
            var output = new StringBuilder();
            _popup.SetText(string.Empty);
            await foreach (var chunk in coordinator.TranslateAsync(
                               new TranslationRequest(result.Text!, TargetLanguage: SelectedTargetLanguage()),
                               _popupCancellation.Token))
            {
                output.Append(chunk.TextDelta);
                _popup.SetText(output.ToString());
            }
        }
        catch (OperationCanceledException)
        {
            if (!_popupCancellation.IsCancellationRequested) _popup.SetError("翻译已取消。");
        }
        catch (Exception exception)
        {
            _popup.SetError(exception.Message);
        }
    }

    private void PopupMoved(object? sender, PopupMovedEventArgs e)
    {
        _settings = _settings with
        {
            PopupOffsetX = e.Offset.X,
            PopupOffsetY = e.Offset.Y,
        };
        _ = PersistSettingsAsync(_settings);
    }

    private PopupOffset? SavedPopupOffset() =>
        _settings.PopupOffsetX is double x && _settings.PopupOffsetY is double y
            ? new PopupOffset(x, y)
            : null;

    private async Task PersistSettingsAsync(YitaSettings settings)
    {
        try { await _settingsStore.SaveAsync(settings).ConfigureAwait(false); }
        catch { /* A transient settings write must not break the popup. */ }
    }

    private DeepSeekStreamingTranslator CreateTranslator()
    {
        var endpoint = new Uri((EndpointBox.Text ?? _settings.DeepSeekEndpoint).Trim());
        var model = (ModelBox.Text ?? _settings.DeepSeekModel).Trim();
        var key = (_apiKeyField.Text ?? string.Empty).Trim();
        return new DeepSeekStreamingTranslator(_httpClient, new TranslationProviderOptions(endpoint, model, key));
    }

    private string SelectedTargetLanguage() =>
        (TargetLanguageBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "简体中文";

    private static void SetStatus(TextBlock target, string message, bool error)
    {
        target.Text = message;
        target.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(error ? "#B34335" : "#155E63"));
    }
}
