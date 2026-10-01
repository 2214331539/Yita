using System.Net.Http;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yita.Core;
using Yita.Core.Placement;
using Yita.Core.Settings;
using Yita.Core.Selection;
using Yita.Core.Translation;
using Yita.Native.Mac;
using Yita.Native.Windows;

namespace Yita.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly ISettingsStore _settingsStore;
    private readonly ISecretStore _secretStore;
    private MemoryTranslationCache _translationCache = new();
    private string? _cacheProvider;
    private readonly JsonlTranslationHistoryStore _historyStore;
    private readonly WindowsSelectionRuntime? _windowsRuntime;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly LatestRequestController _manualRequests = new();
    private readonly LatestRequestController _historyRequests = new();
    private readonly LatestRequestController _connectionRequests = new();
    private readonly HashSet<TranslationPopupWindow> _popups = new();
    private readonly List<HistoryListItem> _historyEntries = new();
    private TranslationPopupWindow? _popup;
    private YitaSettings _settings = YitaSettings.Default;
    private string _savedApiKey = string.Empty;
    private Task? _initialization;
    private bool _shuttingDown;

    public bool IsSelectionTranslationEnabled => _settings.IsEnabled;
    internal IReadOnlyCollection<TranslationPopupWindow> TranslationPopups => _popups;
    public event EventHandler? SettingsChanged;

    public MainWindow() : this(null) { }

    public MainWindow(WindowsSelectionRuntime? windowsRuntime,
        ISettingsStore? settingsStore = null, ISecretStore? secretStore = null,
        JsonlTranslationHistoryStore? historyStore = null, HttpClient? httpClient = null)
    {
        _windowsRuntime = windowsRuntime;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yita");
        // Import the WPF configuration once; preview saves must not rewrite it.
        _settingsStore = settingsStore ?? new JsonSettingsStore(Path.Combine(directory, "desktop-settings.json"),
            Path.Combine(directory, "settings.json"));
        _secretStore = secretStore ?? CreateSecretStore();
        _historyStore = historyStore ?? new JsonlTranslationHistoryStore(Path.Combine(directory, "desktop-history.jsonl"));
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        InitializeComponent();
        Opened += async (_, _) => await InitializeAsync();
        Closed += (_, _) => ShutdownServices();
    }

    private Task InitializeAsync() => _initialization ??= LoadSettingsAsync();

    private async Task LoadSettingsAsync()
    {
        try
        {
            _settings = await _settingsStore.LoadAsync();
            if (_shuttingDown) return;
            _settings = _settings with
            {
                SelectionDelayMilliseconds = Math.Clamp(_settings.SelectionDelayMilliseconds, 0, 2000),
                MaximumSelectionCharacters = Math.Clamp(_settings.MaximumSelectionCharacters, 100, 50000),
            };
            EnabledCheckBox.IsChecked = _settings.IsEnabled;
            StartCheckBox.IsChecked = _settings.StartWithSystem;
            ClipboardCheckBox.IsChecked = _settings.UseClipboardFallback;
            HistoryCheckBox.IsChecked = _settings.AiHistoryEnabled;
            SelectionDelayBox.Value = Math.Clamp(_settings.SelectionDelayMilliseconds, 0, 2000);
            MaximumCharactersBox.Value = Math.Clamp(_settings.MaximumSelectionCharacters, 100, 50000);
            EndpointBox.Text = _settings.DeepSeekEndpoint;
            ModelBox.Text = _settings.DeepSeekModel;
            TargetLanguageBox.SelectedIndex = _settings.TargetLanguage switch { "English" => 1, "日本語" => 2, _ => 0 };
            ApplyRuntimeSettings();
            SetStatus(GeneralStatus, _windowsRuntime is { IsRunning: true }
                ? "划词捕获已就绪。" : "当前未启动系统划词捕获。", _windowsRuntime is null);
            try
            {
                var key = await _secretStore.ReadApiKeyAsync() ?? string.Empty;
                if (!_shuttingDown) ApiKeyField.Text = _savedApiKey = key;
            }
            catch { SetStatus(ModelStatus, "无法读取系统凭据，请重新填写 API Key。", true); }
        }
        catch (Exception exception) { SetStatus(GeneralStatus, $"加载设置失败：{exception.Message}", true); }
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
            "History" => ("AI 记录", "翻译历史"),
            "Appearance" => ("翻译与外观", "语言与翻译"),
            "Model" => ("模型配置", "翻译服务"),
            _ => ("常规", "运行设置"),
        };
        if (page == "History") _ = RefreshHistoryAsync();
    }

    private async void SaveSettingsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeAsync();
            _settings = _settings with
            {
                IsEnabled = EnabledCheckBox.IsChecked == true,
                StartWithSystem = StartCheckBox.IsChecked == true,
                UseClipboardFallback = ClipboardCheckBox.IsChecked == true,
                AiHistoryEnabled = HistoryCheckBox.IsChecked == true,
                SelectionDelayMilliseconds = (int)(SelectionDelayBox.Value ?? 80),
                MaximumSelectionCharacters = (int)(MaximumCharactersBox.Value ?? 8000),
                TargetLanguage = SelectedTargetLanguage(),
                DeepSeekEndpoint = (EndpointBox.Text ?? string.Empty).Trim(),
                DeepSeekModel = (ModelBox.Text ?? string.Empty).Trim(),
            };
            await PersistSettingsAsync();
            var key = (ApiKeyField.Text ?? string.Empty).Trim();
            await _secretStore.SaveApiKeyAsync(key);
            _savedApiKey = key;
            ApplyRuntimeSettings();
            if (OperatingSystem.IsWindows() && _windowsRuntime is not null)
                WindowsStartupRegistration.Apply(_settings.StartWithSystem);
            SetStatus(SaveStatus, "设置已保存。", false);
        }
        catch (Exception exception) { SetStatus(SaveStatus, $"保存失败：{exception.Message}", true); }
    }

    private void ApplyRuntimeSettings()
    {
        _windowsRuntime?.Configure(_settings.IsEnabled, _settings.UseClipboardFallback, _settings.SelectionDelayMilliseconds);
        if (!_settings.IsEnabled)
            foreach (var popup in _popups) popup.TranslationRequests.Cancel();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ToggleEnabledFromTray()
    {
        if (_shuttingDown || _initialization is not { IsCompleted: true }) return;
        _settings = _settings with { IsEnabled = !_settings.IsEnabled };
        EnabledCheckBox.IsChecked = _settings.IsEnabled;
        ApplyRuntimeSettings();
        _ = PersistSettingsWithStatusAsync();
    }

    private async void TestConnectionClick(object? sender, RoutedEventArgs e)
    {
        if (_shuttingDown) return;
        using var pending = _connectionRequests.Begin();
        try
        {
            SetStatus(ModelStatus, "正在连接…", false);
            await foreach (var chunk in CreateTranslator(false).TranslateAsync(new TranslationRequest("hello"), pending.Token))
            {
                if (!pending.IsCurrent) return;
                if (string.IsNullOrEmpty(chunk.TextDelta)) continue;
                SetStatus(ModelStatus, "连接成功。", false);
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (pending.IsCurrent) SetStatus(ModelStatus, exception.Message, true); }
    }

    private async void TranslateClick(object? sender, RoutedEventArgs e)
    {
        if (_shuttingDown) return;
        using var pending = _manualRequests.Begin();
        try
        {
            await InitializeAsync();
            if (!pending.IsCurrent) return;
            var source = SourceTextBox.Text ?? string.Empty;
            ValidateSelection(source);
            var output = new StringBuilder();
            TranslationText.Text = string.Empty;
            SetStatus(TranslationStatus, "正在翻译…", false);
            await foreach (var chunk in CreateCoordinator(false).TranslateAsync(CreateRequest(source, false), pending.Token))
            {
                if (!pending.IsCurrent) return;
                output.Append(chunk.TextDelta);
                TranslationText.Text = output.ToString();
            }
            if (pending.IsCurrent) SetStatus(TranslationStatus, "翻译完成。", false);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (pending.IsCurrent) SetStatus(TranslationStatus, exception.Message, true); }
    }

    private void CancelClick(object? sender, RoutedEventArgs e)
    {
        _manualRequests.Cancel();
        SetStatus(TranslationStatus, "翻译已取消。", false);
    }

    internal async Task ShowSelectionTranslationAsync(SelectionRequest request, SelectionResult result)
    {
        TranslationPopupWindow? popup = null;
        LatestRequestController.RequestLease? pending = null;
        try
        {
            if (_shuttingDown) return;
            await InitializeAsync();
            if (!_settings.IsEnabled || _shuttingDown) return;
            if (!result.Succeeded && request.Trigger == SelectionTrigger.MouseGesture) return;
            if (_popup is null || _popup.IsPinned) _popup = CreatePopup();
            popup = _popup;
            pending = popup.TranslationRequests.Begin();
            popup.PlaceNear(request, result, SavedPopupOffset());
            popup.BeginTranslation(result.Text ?? string.Empty);
            if (!popup.IsVisible) popup.Show();
            if (!result.Succeeded)
            {
                popup.SetError(result.Failure switch
                {
                    SelectionFailureKind.PermissionDenied => "没有读取选区的权限。",
                    SelectionFailureKind.Empty => "没有读取到选中文字，请保持选区后重试。",
                    SelectionFailureKind.UnsupportedApplication => "当前应用无法读取选区。",
                    _ => "无法读取当前选区。",
                });
                return;
            }
            ValidateSelection(result.Text!);
            var output = new StringBuilder();
            await foreach (var chunk in CreateCoordinator(true).TranslateAsync(CreateRequest(result.Text!, true), pending.Token))
            {
                if (!pending.IsCurrent) return;
                output.Append(chunk.TextDelta);
                popup.SetText(output.ToString());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (_shuttingDown || pending is { IsCurrent: false }) return;
            if (popup is { IsVisible: true }) popup.SetError(exception.Message);
            else SetStatus(GeneralStatus, "无法显示翻译浮窗，请重试。", true);
        }
        finally { pending?.Dispose(); }
    }

    private TranslationPopupWindow CreatePopup()
    {
        var popup = new TranslationPopupWindow();
        _popups.Add(popup);
        popup.Moved += PopupMoved;
        popup.Closed += (_, _) =>
        {
            _popups.Remove(popup);
            if (ReferenceEquals(popup, _popup)) _popup = null;
        };
        popup.Dismissed += (_, _) =>
        {
            if (ReferenceEquals(popup, _popup)) return;
            popup.Close();
        };
        return popup;
    }

    private void PopupMoved(object? sender, PopupMovedEventArgs e)
    {
        _settings = _settings with { PopupOffsetX = e.Offset.X, PopupOffsetY = e.Offset.Y };
        _ = PersistSettingsWithStatusAsync();
    }

    private PopupOffset? SavedPopupOffset() =>
        _settings.PopupOffsetX is double x && _settings.PopupOffsetY is double y ? new PopupOffset(x, y) : null;

    private async Task PersistSettingsAsync()
    {
        await _settingsGate.WaitAsync();
        try { await _settingsStore.SaveAsync(_settings); }
        finally { _settingsGate.Release(); }
    }

    private async Task PersistSettingsWithStatusAsync()
    {
        try { await PersistSettingsAsync(); }
        catch { SetStatus(SaveStatus, "设置暂时无法保存，请稍后重试。", true); }
    }

    private DeepSeekStreamingTranslator CreateTranslator(bool useSavedSettings)
    {
        var endpoint = useSavedSettings ? _settings.DeepSeekEndpoint : EndpointBox.Text;
        var model = useSavedSettings ? _settings.DeepSeekModel : ModelBox.Text;
        var key = useSavedSettings ? _savedApiKey : (ApiKeyField.Text ?? string.Empty).Trim();
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException("请填写有效的 Endpoint。");
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("请填写模型名称。");
        if (key.Length == 0) throw new ArgumentException("请先配置 API Key。");
        return new DeepSeekStreamingTranslator(_httpClient, new TranslationProviderOptions(uri, model.Trim(), key));
    }

    private static ISecretStore CreateSecretStore() => OperatingSystem.IsWindows()
        ? SecretStoreFactory.CreateDefault() : OperatingSystem.IsMacOS() ? new MacKeychainSecretStore() : new MemorySecretStore();

    private TranslationCoordinator CreateCoordinator(bool useSavedSettings)
    {
        var translator = CreateTranslator(useSavedSettings);
        var provider = translator.Options.Endpoint.AbsoluteUri + "\n" + translator.Options.Model;
        if (_cacheProvider != provider) { _translationCache = new MemoryTranslationCache(); _cacheProvider = provider; }
        var coordinator = new TranslationCoordinator(translator, _translationCache,
            _settings.AiHistoryEnabled ? _historyStore : null);
        coordinator.HistoryWriteFailed += _ => Dispatcher.UIThread.Post(() =>
            SetStatus(HistoryStatus, "译文已完成，但历史记录保存失败。", true));
        return coordinator;
    }

    private TranslationRequest CreateRequest(string text, bool useSavedSettings) => new(text,
        _settings.SourceLanguage, useSavedSettings ? _settings.TargetLanguage : SelectedTargetLanguage(),
        Mode: _settings.TranslationMode, Tone: _settings.TranslationTone);

    private void ValidateSelection(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("请先选择或输入待翻译文本。");
        if (text.Length > _settings.MaximumSelectionCharacters)
            throw new ArgumentException($"所选文本超过 {_settings.MaximumSelectionCharacters} 字符，请缩小选区。");
    }

    private string SelectedTargetLanguage() =>
        (TargetLanguageBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "简体中文";

    private async void RefreshHistoryClick(object? sender, RoutedEventArgs e) => await RefreshHistoryAsync();

    private async Task RefreshHistoryAsync()
    {
        if (_shuttingDown) return;
        using var pending = _historyRequests.Begin();
        try
        {
            var entries = await Task.Run(async () =>
            {
                var recent = new Queue<HistoryListItem>();
                await foreach (var entry in _historyStore.ReadAsync(pending.Token))
                {
                    recent.Enqueue(new HistoryListItem(entry));
                    if (recent.Count > 500) recent.Dequeue();
                }
                return recent;
            }, pending.Token);
            if (!pending.IsCurrent) return;
            _historyEntries.Clear();
            _historyEntries.AddRange(entries.Reverse());
            FilterHistory();
            SetStatus(HistoryStatus, _historyEntries.Count == 0 ? "暂无翻译记录。" : $"最近 {_historyEntries.Count} 条记录", false);
        }
        catch (OperationCanceledException) { }
        catch { if (pending.IsCurrent) SetStatus(HistoryStatus, "无法读取历史记录。", true); }
    }

    private void HistorySearchChanged(object? sender, TextChangedEventArgs e) => FilterHistory();

    private void FilterHistory()
    {
        if (HistoryList is null) return;
        var query = HistorySearchBox.Text?.Trim() ?? string.Empty;
        HistoryList.ItemsSource = _historyEntries.Where(item =>
            item.Entry.SourceText.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Entry.TranslationText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void HistorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var item = HistoryList.SelectedItem as HistoryListItem;
        HistorySourceText.Text = item?.Entry.SourceText ?? string.Empty;
        HistoryTranslatedText.Text = item?.Entry.TranslationText ?? string.Empty;
    }

    private void RequestClearHistoryClick(object? sender, RoutedEventArgs e) => ClearHistoryConfirmation.IsVisible = true;
    private void CancelClearHistoryClick(object? sender, RoutedEventArgs e) => ClearHistoryConfirmation.IsVisible = false;

    private async void ConfirmClearHistoryClick(object? sender, RoutedEventArgs e)
    {
        ClearHistoryConfirmation.IsVisible = false;
        _historyRequests.Cancel();
        try { await _historyStore.ClearAsync(); await RefreshHistoryAsync(); }
        catch { SetStatus(HistoryStatus, "无法清空历史记录。", true); }
    }

    internal void ShutdownServices()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        _manualRequests.Dispose();
        _historyRequests.Dispose();
        _connectionRequests.Dispose();
        foreach (var popup in _popups.ToArray()) { popup.TranslationRequests.Cancel(); popup.Close(); }
        _popups.Clear();
        _httpClient.Dispose();
    }

    private static void SetStatus(TextBlock target, string message, bool error)
    {
        target.Text = message;
        target.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(error ? "#B34335" : "#155E63"));
    }
}
