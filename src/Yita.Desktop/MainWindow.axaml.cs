using System.Globalization;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Yita.Core;
using Yita.Core.Platform;
using Yita.Core.Parity;
using Yita.Core.Selection;
using Yita.Core.Settings;
using Yita.Core.Translation;
using Yita.Services;
using Yita.Settings;
using Yita.Translation;

namespace Yita.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly ISettingsStore _settingsStore;
    private readonly ISecretStore _secretStore;
    private readonly ISelectionRuntime? _selectionRuntime;
    private readonly IStartupRegistration _startupRegistration;
    private readonly IPlatformPermissionService? _permissionService;
    private readonly ITranslationProviderFactory _providerFactory;
    private ReferenceTranslationRuntime _translationRuntime;
    private TranslationMemoryStore? _memory;
    private IDisposable? _memoryProtector;
    private readonly bool _usePlatformMemory;
    private readonly DesktopPlatformServices _platform = new();
    private readonly HttpClient? _injectedClient;
    private readonly AiHistoryStore _records = new();
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly LatestRequestController _connectionRequests = new();
    private readonly LatestRequestController _summaryRequests = new();
    private YitaSettings _settings = YitaSettings.Default;
    private string _savedApiKey = string.Empty;
    private string _uiLanguage = "en";
    private bool _ready;
    private bool _apiKeyClearRequested;
    private bool _shuttingDown;
    private Task? _initialization;
    private SettingsFailureKind? _settingsFailure;
    private bool _memoryInitializationFailed;
    private bool _credentialsAvailable;

    public bool IsSelectionTranslationEnabled => _settings.IsEnabled;
    public event EventHandler? SettingsChanged;
    internal event EventHandler<string>? UiLanguageChanged;
    internal Task Initialization => InitializeAsync();
    internal YitaSettings SavedSettings => _settings;

    public MainWindow() : this(null) { }

    public MainWindow(ISelectionRuntime? selectionRuntime, ISettingsStore? settingsStore = null,
        ISecretStore? secretStore = null, JsonlTranslationHistoryStore? historyStore = null, HttpClient? httpClient = null,
        IStartupRegistration? startupRegistration = null, IPlatformPermissionService? permissionService = null)
    {
        _selectionRuntime = selectionRuntime;
        var platform = _platform;
        _startupRegistration = startupRegistration ?? platform.Startup;
        _permissionService = permissionService ?? selectionRuntime as IPlatformPermissionService ?? platform.CreatePermissionService();
        var directory = platform.DataDirectory;
        _settingsStore = settingsStore ?? new JsonSettingsStore(Path.Combine(directory, "desktop-settings.json"),
            Path.Combine(directory, "settings.json"));
        _secretStore = secretStore ?? platform.CreateSecretStore();
        _injectedClient = httpClient;
        _providerFactory = httpClient is null ? new TranslationProviderFactory() : new InjectedTranslationProviderFactory(httpClient);
        _usePlatformMemory = settingsStore is null;
        _translationRuntime = new ReferenceTranslationRuntime(_providerFactory, _memory);
        InitializeComponent();
        _ready = true;
        ModelComboBox.ItemsSource = new[] { "deepseek-v4-flash", "deepseek-v4-pro", "deepseek-chat", "deepseek-reasoner" };
        AiHistoryEnabledCheckBox.IsCheckedChanged += (_, _) => UpdateAiHistoryControls();
        Opened += async (_, _) => { await InitializeAsync(); ReferenceMotion.Reveal(SettingsRoot); };
        Closed += (_, _) => ShutdownServices();
        if (_selectionRuntime is not null)
        {
            _selectionRuntime.SelectionCaptured += OnSelectionCaptured;
            _selectionRuntime.ExternalPointerPressed += OnExternalPointerPressed;
            if (_selectionRuntime is Yita.Native.Mac.MacSelectionRuntime mac) mac.StatusChanged += OnNativeInputStatusChanged;
        }
    }

    private Task InitializeAsync() => _initialization ??= LoadSettingsAsync();

    private async Task LoadSettingsAsync()
    {
        try
        {
            try { _settings = await _settingsStore.LoadAsync(); }
            catch (SettingsStoreException exception)
            {
                _settingsFailure = exception.Kind;
                _settings = YitaSettings.Default with { IsEnabled = false };
            }
            try { _savedApiKey = await _secretStore.ReadApiKeyAsync() ?? ""; _credentialsAvailable = true; }
            catch { SetStatus(ConnectionStatusText, Localize("Could not read credentials. Unlock the system credential store or enter your API key again.", "无法读取凭据，请解锁系统凭据存储或重新填写 API Key。"), true); }
            if (_usePlatformMemory) await InitializeMemoryAsync();
            if (_permissionService is not null) await RefreshPlatformPermissionsAsync();
            if (_shuttingDown) return;
            PopulateSettings();
            ApplyRuntimeSettings();
        }
        catch (Exception exception) { await ShowMessageAsync(Localize("Could not load settings: ", "加载设置失败：") + exception.Message); }
    }

    private async Task InitializeMemoryAsync()
    {
        ITranslationMemoryProtector? protector = null;
        try
        {
            var path = Path.Combine(_platform.DataDirectory, "desktop-translation-memory.dat");
            protector = await _platform.CreateMemoryProtectorAsync(path);
            if (protector is null) return;
            var memory = await Task.Run(() => new TranslationMemoryStore(path, protector));
            if (_shuttingDown) { (protector as IDisposable)?.Dispose(); return; }
            _memoryProtector = protector as IDisposable;
            _memory = memory;
            _translationRuntime = new ReferenceTranslationRuntime(_providerFactory, memory);
        }
        catch
        {
            (protector as IDisposable)?.Dispose();
            _memoryInitializationFailed = true;
        }
    }

    private void PopulateSettings()
    {
        _ready = false;
        var value = _settings.ToOriginal(_savedApiKey);
        EnabledCheckBox.IsChecked = value.IsEnabled;
        EnabledCheckBox.IsEnabled = _selectionRuntime is not null;
        StartWithWindowsCheckBox.IsChecked = value.StartWithWindows;
        StartWithWindowsCheckBox.IsEnabled = _startupRegistration.IsSupported;
        ClipboardFallbackCheckBox.IsChecked = value.UseClipboardFallback;
        ClipboardFallbackCheckBox.IsEnabled = _selectionRuntime is not null;
        WpsPdfCompatibilityCheckBox.IsChecked = value.UseWpsPdfCompatibility;
        WpsPdfCompatibilityCheckBox.IsEnabled = OperatingSystem.IsWindows() && _selectionRuntime is not null;
        UseSelectionContextCheckBox.IsChecked = value.UseSelectionContext;
        SelectionDelayTextBox.Text = value.SelectionDelayMilliseconds.ToString(CultureInfo.InvariantCulture);
        MaximumSelectionTextBox.Text = value.MaximumSelectionCharacters.ToString(CultureInfo.InvariantCulture);
        SetCombo(SourceLanguageComboBox, value.SourceLanguage);
        SetCombo(TargetLanguageComboBox, value.TargetLanguageMode == "auto" ? "自动判断" : value.TargetLanguage);
        SetCombo(TranslationModeComboBox, value.TranslationMode);
        SetCombo(TranslationToneComboBox, value.TranslationTone);
        PersonalGlossaryTextBox.Text = value.PersonalGlossary;
        SetCombo(EnglishTranslationFontComboBox, value.EnglishTranslationFontFamily);
        SetCombo(ChineseTranslationFontComboBox, value.ChineseTranslationFontFamily);
        SetCombo(ColorThemeComboBox, value.ColorTheme);
        CustomAccentColorTextBox.Text = value.CustomAccentColor;
        SetCombo(PopupVisualStyleComboBox, value.PopupVisualStyle);
        DefaultFontSizeSlider.Value = value.DefaultTranslationFontSize;
        SetCombo(ProviderComboBox, value.ProviderId);
        ModelComboBox.Text = value.DeepSeekModel;
        EndpointTextBox.Text = value.DeepSeekEndpoint;
        ApiKeyPasswordBox.Text = _savedApiKey;
        _apiKeyClearRequested = false;
        AiHistoryEnabledCheckBox.IsChecked = value.AiHistoryEnabled;
        AiHistoryDirectoryTextBox.Text = value.AiHistoryDirectory;
        SetCombo(AiSummaryRangeComboBox, SummaryRangeCatalog.ToStableId(value.AiSummaryRange));
        _ready = true;
        ApplyUiLanguage(value.UiLanguage);
        UpdateProviderFields();
        UpdateThemePreview();
        UpdateMemoryStatus();
        UpdateAiHistoryControls();
    }

    private YitaSettings ReadSettings(bool validate)
    {
        if (!int.TryParse(SelectionDelayTextBox.Text, out var delay) || delay is < 0 or > 1000)
            throw new ArgumentException(L("DelayError"));
        if (!int.TryParse(MaximumSelectionTextBox.Text, out var maximum) || maximum is < 100 or > 20000)
            throw new ArgumentException(L("MaximumSelectionError"));
        var theme = ReadCombo(ColorThemeComboBox);
        var color = CustomAccentColorTextBox.Text?.Trim() ?? "";
        if (theme == "custom" && !ThemeCatalog.TryNormalizeHexColor(color, out color))
            throw new ArgumentException(L("CustomColorError"));
        var provider = ReadCombo(ProviderComboBox);
        var endpoint = EndpointTextBox.Text?.Trim() ?? "";
        var model = ModelComboBox.Text?.Trim() ?? "";
        if (provider == "deepseek")
        {
            if (!TranslationProviderFactory.TryValidateEndpoint(endpoint, out var uri)) throw new ArgumentException(L("EndpointError"));
            endpoint = uri!.ToString().TrimEnd('/');
            if (model.Length == 0) throw new ArgumentException(L("ModelEmptyError"));
            if (validate && string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Text) && !_apiKeyClearRequested)
                throw new ArgumentException(L("ApiKeyEmptyError"));
        }
        var folder = AiHistoryDirectoryTextBox.Text?.Trim() ?? "";
        if (validate)
        {
            if (AiHistoryEnabledCheckBox.IsChecked == true && folder.Length == 0) throw new ArgumentException(L("HistoryDirectoryRequired"));
            if (folder.Length > 0 && (!Path.IsPathFullyQualified(folder) || !AiHistoryStore.TryValidateWritableDirectory(folder, out _)))
                throw new ArgumentException(L("HistoryDirectoryUnavailable"));
        }
        var target = ReadCombo(TargetLanguageComboBox);
        return _settings with
        {
            UiLanguage = _uiLanguage, IsEnabled = EnabledCheckBox.IsChecked == true,
            StartWithSystem = _startupRegistration.IsSupported && StartWithWindowsCheckBox.IsChecked == true,
            UseClipboardFallback = ClipboardFallbackCheckBox.IsChecked == true,
            UseWpsPdfCompatibility = WpsPdfCompatibilityCheckBox.IsChecked == true,
            UseSelectionContext = UseSelectionContextCheckBox.IsChecked == true,
            SelectionDelayMilliseconds = delay, MaximumSelectionCharacters = maximum,
            SourceLanguage = ReadCombo(SourceLanguageComboBox), TargetLanguageMode = target == "自动判断" ? "auto" : "fixed",
            TargetLanguage = target == "自动判断" ? "简体中文" : target,
            TranslationMode = ReadCombo(TranslationModeComboBox), TranslationTone = ReadCombo(TranslationToneComboBox),
            PersonalGlossary = PersonalGlossaryTextBox.Text?.Trim() ?? "",
            EnglishTranslationFontFamily = ReadCombo(EnglishTranslationFontComboBox),
            ChineseTranslationFontFamily = ReadCombo(ChineseTranslationFontComboBox),
            ColorTheme = theme, CustomAccentColor = color, PopupVisualStyle = ReadCombo(PopupVisualStyleComboBox),
            DefaultTranslationFontSize = Math.Round(DefaultFontSizeSlider.Value, 1),
            ProviderId = provider, DeepSeekEndpoint = endpoint, DeepSeekModel = model,
            AiHistoryEnabled = AiHistoryEnabledCheckBox.IsChecked == true, AiHistoryDirectory = folder,
            AiSummaryRange = SummaryRangeCatalog.FromStableId(ReadCombo(AiSummaryRangeComboBox)).ToString(),
        };
    }

    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeAsync();
            if (_settingsFailure is not null) return;
            var settings = ReadSettings(true);
            var key = ApiKeyPasswordBox.Text?.Trim() ?? "";
            await _settingsGate.WaitAsync();
            try
            {
                await _settingsStore.ValidateWriteAsync();
                await _secretStore.SaveApiKeyAsync(key);
                await _settingsStore.SaveAsync(settings);
                _settings = settings;
                _savedApiKey = key;
                _credentialsAvailable = true;
            }
            finally { _settingsGate.Release(); }
            if (_startupRegistration.IsSupported && _selectionRuntime is not null) _startupRegistration.Apply(_settings.StartWithSystem);
            ApplyRuntimeSettings();
            Hide();
        }
        catch (SettingsStoreException exception)
        {
            _settingsFailure = exception.Kind;
            ApplyUiLanguage(_uiLanguage);
        }
        catch (Exception exception) { await ShowMessageAsync(exception.Message); }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) { PopulateSettings(); Hide(); }

    private void ApplyRuntimeSettings()
    {
        _selectionRuntime?.Configure(_settings.IsEnabled, _settings.UseClipboardFallback, _settings.SelectionDelayMilliseconds,
            _settings.UseWpsPdfCompatibility, _settings.UseSelectionContext);
        ReferenceTheme.Apply(Application.Current!.Resources, _settings.ToOriginal());
        foreach (var popup in _popups) popup.ApplySettings(_settings.ToOriginal(_savedApiKey));
        SynchronizeWindowLanguage();
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

    private async Task PersistSettingsWithStatusAsync()
    {
        if (_settingsFailure is not null) return;
        try
        {
            await _settingsGate.WaitAsync();
            try { await _settingsStore.SaveAsync(_settings); }
            finally { _settingsGate.Release(); }
        }
        catch { SetStatus(ConnectionStatusText, Localize("Could not save settings.", "暂时无法保存设置。"), true); }
    }

    private void SettingsNavigationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (GeneralPage is null) return;
        var index = SettingsNavigation.SelectedIndex;
        GeneralPage.IsVisible = index == 0;
        HistoryPage.IsVisible = index == 1;
        AppearancePage.IsVisible = index == 2;
        ModelPage.IsVisible = index == 3;
        SettingsScrollViewer.Offset = default;
        if (_ready) ReferenceMotion.Reveal(new Control[] { GeneralPage, HistoryPage, AppearancePage, ModelPage }[Math.Clamp(index, 0, 3)]);
    }

    private void UiLanguageButton_Click(object? sender, RoutedEventArgs e) => ApplyUiLanguage(_uiLanguage == "en" ? "zh-CN" : "en");

    private void ApplyUiLanguage(string language)
    {
        _uiLanguage = UiLanguageCatalog.Normalize(language);
        Title = L("WindowTitle");
        UiLanguageButton.Content = _uiLanguage == "en" ? "中文" : "English";
        foreach (var control in this.GetLogicalDescendants().OfType<Control>())
        {
            if (control.Tag is not string tag || !tag.StartsWith("loc:", StringComparison.Ordinal)) continue;
            if (control is TextBlock text) text.Text = L(tag[4..]);
            else if (control is ContentControl content) content.Content = L(tag[4..]);
        }
        var navKeys = new[] { "NavigationGeneral", "NavigationAiHistory", "NavigationTranslationAppearance", "NavigationModel" };
        for (var i = 0; i < navKeys.Length; i++) ((ListBoxItem)SettingsNavigation.Items[i]!).Content = L(navKeys[i]);
        LocalizeCombo(SourceLanguageComboBox, [("自动检测", "Auto detect", "自动检测"), ("英语", "English", "英语"), ("简体中文", "Simplified Chinese", "简体中文")]);
        LocalizeCombo(TargetLanguageComboBox, [("自动判断", "Choose automatically", "自动判断"), ("简体中文", "Simplified Chinese", "简体中文"), ("英语", "English", "英语"), ("日语", "Japanese", "日语")]);
        LocalizeCombo(TranslationModeComboBox, [("fast", "Fast", "快速"), ("balanced", "Balanced", "均衡"), ("precise", "Precise", "精确")]);
        LocalizeCombo(TranslationToneComboBox, [("natural", "Natural", "自然"), ("formal", "Formal", "正式"), ("concise", "Concise", "简洁"), ("academic", "Academic", "学术"), ("technical", "Technical", "技术")]);
        LocalizeCombo(AiSummaryRangeComboBox, [("today", "Today", "今天"), ("last-7-days", "Last 7 days", "最近 7 天"), ("all", "All", "全部")]);
        LocalizeCombo(ColorThemeComboBox, [("yita", "Yita · Lakeside", "译獭 · 湖畔"), ("ocean", "Ocean blue", "深海蓝"), ("violet", "Violet", "紫罗兰"), ("emerald", "Emerald", "翡翠绿"), ("sunset", "Warm orange", "暖橙色"), ("rose", "Rose", "玫瑰红"), ("custom", "Custom", "自定义")]);
        LocalizeCombo(PopupVisualStyleComboBox, [("minimal", "Minimal", "极简"), ("bubble", "Bubble", "气泡"),
            ("bubble-v2", "Bubble 2.0", "气泡 2.0"), ("bubble-v3", "Bubble 3.0 · Glass", "气泡 3.0 · 液态玻璃"),
            ("bubble-v3-color", "Bubble 3.0 · Color Glass", "气泡 3.0 · 彩色玻璃")]);
        LocalizeCombo(ProviderComboBox, [("deepseek", "DeepSeek API", "DeepSeek API"), ("mock", "Mock · offline test", "Mock · 离线测试")]);
        ToolTip.SetTip(SelectionDelayTextBox, L("SelectionDelayTooltip"));
        ToolTip.SetTip(MaximumSelectionTextBox, L("MaximumSelectionTooltip"));
        ToolTip.SetTip(CustomAccentColorTextBox, L("CustomColorTooltip"));
        SettingsStatusText.IsVisible = _settingsFailure is not null;
        SettingsStatusText.Text = _settingsFailure == SettingsFailureKind.NewerVersion
            ? Localize("These settings require a newer Yita version. Editing is disabled; the file has been preserved.", "设置来自更高版本的 Yita，已禁止保存并保留原文件，请更新软件。")
            : Localize("Settings could not be read. Saving is disabled; the file has been preserved.", "无法读取设置，已禁止保存并保留原文件。请检查或恢复设置文件。");
        SaveSettingsButton.IsEnabled = _settingsFailure is null;
        UpdateInputAvailability();
        UpdatePermissionControls();
        if (OperatingSystem.IsMacOS())
            TranslationServiceDescriptionText.Text = Localize("Your API key is stored in macOS Keychain on this device.", "API Key 保存在本机 macOS Keychain 中。");
        else if (!OperatingSystem.IsWindows())
            TranslationServiceDescriptionText.Text = Localize("Your API key is kept for this session only on this platform.", "此平台仅在当前运行期间保留 API Key。");
        if (!_startupRegistration.IsSupported)
            ToolTip.SetTip(StartWithWindowsCheckBox, Localize("Login startup is not available on this platform yet.", "此平台尚未支持登录时启动。"));
        UpdateMemoryStatus();
        UpdateThemePreview();
        SynchronizeWindowLanguage();
        UiLanguageChanged?.Invoke(this, _uiLanguage);
    }

    private void SynchronizeWindowLanguage()
    {
        foreach (var popup in _popups) popup.ApplyUiLanguage(_uiLanguage);
        foreach (var conversation in _conversations) conversation.ApplyUiLanguage(_uiLanguage);
        foreach (var session in _sessions.Values) session.Settings = session.Settings with { UiLanguage = _uiLanguage };
    }

    private void LocalizeCombo(ComboBox combo, (string Value, string English, string Chinese)[] labels)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
            foreach (var label in labels)
                if (item.Tag?.ToString() == label.Value) item.Content = Localize(label.English, label.Chinese);
        // Avalonia snapshots ComboBoxItem.Content into SelectionBoxItem.
        // Reselect the same option so its visible label follows a language change.
        var selected = combo.SelectedIndex;
        var ready = _ready;
        _ready = false;
        try { combo.SelectedIndex = -1; combo.SelectedIndex = selected; }
        finally { _ready = ready; }
    }

    private void ProviderComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) { if (_ready) UpdateProviderFields(); }
    private void ModelPickerArrowClick(object? sender, RoutedEventArgs e)
    {
        if (!ModelComboBox.IsEnabled || sender is not Button button) return;
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var choices = new StackPanel { MinWidth = Math.Max(180, ModelComboBox.Bounds.Width - 20) };
        foreach (var model in ModelComboBox.ItemsSource!.Cast<string>())
        {
            var choice = new Button { Content = model, Classes = { "tray-command" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            choice.Click += (_, _) => { ModelComboBox.Text = model; flyout.Hide(); };
            choices.Children.Add(choice);
        }
        flyout.Content = choices;
        flyout.ShowAt(button);
    }
    private void UpdateProviderFields()
    {
        var enabled = ReadCombo(ProviderComboBox) == "deepseek";
        EndpointTextBox.IsEnabled = ModelComboBox.IsEnabled = ApiKeyPasswordBox.IsEnabled = enabled;
    }

    private void ColorThemeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) { if (_ready) UpdateThemePreview(); }
    private void CustomAccentColorTextBox_TextChanged(object? sender, TextChangedEventArgs e) { if (_ready) UpdateThemePreview(); }
    private void PopupVisualStyleComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) { if (_ready) UpdateThemePreview(); }
    private void HighlightPaletteComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) { }

    private void UpdateThemePreview()
    {
        CustomAccentColorTextBox.IsEnabled = ReadCombo(ColorThemeComboBox) == "custom";
        var palette = ThemeCatalog.Resolve(ReadCombo(ColorThemeComboBox), CustomAccentColorTextBox.Text);
        ThemePreviewBorder.Background = ReferenceTheme.Brush(palette.Accent);
        var style = ReadCombo(PopupVisualStyleComboBox);
        var bubble = PopupVisualStyleCatalog.IsBubble(style);
        var sculpted = PopupVisualStyleCatalog.IsBubbleV2(style) || PopupVisualStyleCatalog.IsBubbleV3(style);
        var glass = PopupVisualStyleCatalog.IsBubbleV3(style);
        PopupStylePreviewSurface.Margin = bubble ? new Thickness(sculpted ? 14 : 12, 2, 0, 2) : new Thickness(0, 4, 0, 4);
        PopupStylePreviewSurface.CornerRadius = new CornerRadius(sculpted ? 22 : bubble ? 18 : 7);
        PopupStylePreviewSurface.Background = ReferenceTheme.PreviewBrush(style, palette);
        PopupStylePreviewSurface.BorderThickness = new Thickness(glass ? 1 : bubble ? 0 : 1);
        PopupStylePreviewSurface.BorderBrush = ReferenceTheme.Brush(glass ? "#A0FFFFFF" : palette.PopupBorder);
        PopupStylePreviewTextSurface.Background = glass ? ReferenceTheme.Brush(palette.PopupBackground) : Brushes.Transparent;
        PopupStylePreviewTail.Data = Geometry.Parse(sculpted ? "M0,6 C3,4 6,1.5 12,0 C10,3.8 10,8.2 12,12 C6,10.5 3,8 0,6 Z" : "M0,0 L12,6 L0,12 Z");
        PopupStylePreviewTail.Fill = PopupStylePreviewSurface.Background;
        PopupStylePreviewTail.IsVisible = bubble;
        PopupStyleGlassHint.IsVisible = glass;
        PopupStyleGlassHint.Text = palette.PopupBackground == "#FFFCF7"
            ? Localize("Warm glass surrounds an opaque ivory reading surface to keep text clear.", "暖色透明外层搭配不透明奶油白阅读面，避免桌面背景干扰正文。")
            : L(PopupVisualStyleCatalog.IsBubbleV3Color(style) ? "PopupStyleColorGlassHint" : "PopupStyleGlassHint");
    }

    private void DefaultFontSizeSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    { if (DefaultFontSizeValueText is not null) DefaultFontSizeValueText.Text = DefaultFontSizeSlider.Value.ToString("0.0", CultureInfo.InvariantCulture); }

    private async void TestConnectionButton_Click(object? sender, RoutedEventArgs e)
    {
        using var pending = _connectionRequests.Begin();
        TestConnectionButton.IsEnabled = false;
        SetStatus(ConnectionStatusText, L("Connecting"), false);
        try
        {
            var settings = ReadSettings(false).ToOriginal(ApiKeyPasswordBox.Text?.Trim() ?? "");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(pending.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            // A credential check must reach the provider, including after a
            // previous successful translation of the same test text.
            var provider = _providerFactory.Create(settings);
            await foreach (var chunk in provider.TranslateAsync(new Yita.Translation.TranslationRequest(
                "hello", "英语", "简体中文", Mode: settings.TranslationMode,
                Tone: settings.TranslationTone, PersonalGlossary: settings.PersonalGlossary), timeout.Token))
            {
                if (!pending.IsCurrent) return;
                if (string.IsNullOrWhiteSpace(chunk.TextDelta)) continue;
                SetStatus(ConnectionStatusText, L("ConnectionSuccess"), false);
                return;
            }
            SetStatus(ConnectionStatusText, L("NoTestTranslation"), true);
        }
        catch (OperationCanceledException) { if (pending.IsCurrent) SetStatus(ConnectionStatusText, L("ConnectionTimeout"), true); }
        catch (Exception exception) { if (pending.IsCurrent) SetStatus(ConnectionStatusText, exception.Message, true); }
        finally { if (pending.IsCurrent) TestConnectionButton.IsEnabled = true; }
    }

    private void ClearApiKeyButton_Click(object? sender, RoutedEventArgs e)
    { ApiKeyPasswordBox.Text = ""; _apiKeyClearRequested = true; SetStatus(ConnectionStatusText, L("DeleteAfterSave"), false); }

    private void AiHistoryDirectoryTextBox_TextChanged(object? sender, TextChangedEventArgs e) { if (_ready) UpdateAiHistoryControls(); }
    private void UpdateAiHistoryControls()
    {
        if (!_ready) return;
        var folder = AiHistoryDirectoryTextBox.Text?.Trim() ?? "";
        OpenAiHistoryDirectoryButton.IsEnabled = folder.Length > 0 && Directory.Exists(folder);
        GenerateAiSummaryButton.IsEnabled = AiHistoryEnabledCheckBox.IsChecked == true && folder.Length > 0;
    }

    private async void BrowseAiHistoryDirectoryButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L("HistoryDirectory"), AllowMultiple = false });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is string path) AiHistoryDirectoryTextBox.Text = path;
        }
        catch (Exception exception) { SetStatus(AiHistoryStatusText, exception.Message, true); }
    }

    private void OpenAiHistoryDirectoryButton_Click(object? sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AiHistoryDirectoryTextBox.Text!) { UseShellExecute = true }); }
        catch { SetStatus(AiHistoryStatusText, L("HistoryDirectoryUnavailable"), true); }
    }

    private async void GenerateAiSummaryButton_Click(object? sender, RoutedEventArgs e)
    {
        using var pending = _summaryRequests.Begin();
        GenerateAiSummaryButton.IsEnabled = false;
        try
        {
            var settings = ReadSettings(true).ToOriginal(ApiKeyPasswordBox.Text?.Trim() ?? "");
            SetStatus(AiHistoryStatusText, L("GeneratingSummary"), false);
            var result = await new AiSummaryService(_records, _providerFactory).GenerateAsync(settings, settings.AiSummaryRange, pending.Token);
            if (pending.IsCurrent) SetStatus(AiHistoryStatusText, result.Succeeded
                ? Localize("Saved: ", "已保存：") + result.OutputPath
                : Localize("Summary unavailable: ", "无法生成总结：") + result.ErrorCode, !result.Succeeded);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (pending.IsCurrent) SetStatus(AiHistoryStatusText, exception.Message, true); }
        finally { if (pending.IsCurrent) UpdateAiHistoryControls(); }
    }

    private void UpdateMemoryStatus()
    {
        var count = _memory?.Count ?? 0;
        TranslationMemoryStatusText.Text = _memoryInitializationFailed || _memory?.LoadFailed == true
            ? Localize("Encrypted memory could not be opened. Existing data has been preserved.", "无法打开加密翻译记忆，已保留原数据。")
            : _memory is null ? Localize("Encrypted memory is unavailable.", "加密翻译记忆不可用。")
            : count == 0 ? L("TranslationMemoryEmpty") : string.Format(L("TranslationMemoryCount"), count);
        ClearTranslationMemoryButton.IsEnabled = _memory is not null && (count > 0 || _memory.LoadFailed);
    }

    private async void ClearTranslationMemoryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!await ShowMessageAsync(L("ClearTranslationMemoryMessage"), true)) return;
        try { _memory?.Clear(); UpdateMemoryStatus(); }
        catch { await ShowMessageAsync(L("ClearTranslationMemoryFailed")); }
    }

    internal async Task<bool> ShowMessageAsync(string message, bool confirm = false)
    {
        var dialog = new Window { Title = "Yita", Width = 420, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = new FontFamily("Microsoft YaHei"), FontSize = 14,
            Background = ReferenceTheme.Brush("#FFFCF7") };
        var stack = new StackPanel { Margin = new Thickness(24), Spacing = 18 };
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        if (confirm)
        {
            var cancel = new Button { Content = L("Cancel"), Classes = { "secondary" } };
            cancel.Click += (_, _) => dialog.Close(false);
            actions.Children.Add(cancel);
        }
        var accepted = false;
        var ok = new Button { Content = Localize("OK", "确定"), Classes = { "primary" }, IsDefault = true };
        ok.Click += (_, _) => { accepted = true; dialog.Close(true); };
        actions.Children.Add(ok);
        stack.Children.Add(actions);
        dialog.Content = stack;
        if (IsVisible) return await dialog.ShowDialog<bool>(this);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closed += (_, _) => completion.TrySetResult(accepted);
        dialog.Show();
        return await completion.Task;
    }

    private string L(string key) => SettingsText.LocalizedText.TryGetValue(key, out var value) ? Localize(value.English, value.Chinese) : key;
    private string Localize(string english, string chinese) => _uiLanguage == "zh-CN" ? chinese : english;
    private static string ReadCombo(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
    private static void SetCombo(ComboBox combo, string value)
    { combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == value) ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault(); }
    private static void SetStatus(TextBlock target, string message, bool error)
    { target.Text = message; target.Foreground = ReferenceTheme.Brush(error ? "#A43E32" : "#24756B"); }
}
