using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Yita.Services;
using Yita.Settings;
using Yita.Translation;
using WpfMessageBox = System.Windows.MessageBox;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfListBoxItem = System.Windows.Controls.ListBoxItem;
using WpfSelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using WpfContentControl = System.Windows.Controls.ContentControl;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

namespace Yita.Windows;

internal partial class SettingsWindow : Window
{
    private static readonly IReadOnlyDictionary<string, (string English, string Chinese)> LocalizedText =
        new Dictionary<string, (string English, string Chinese)>(StringComparer.Ordinal)
        {
            ["WindowTitle"] = ("Yita Settings", "Yita 设置"),
            ["HeaderTitle"] = ("Settings", "设置"),
            ["HeaderSubtitle"] = ("Translation behavior, appearance, and connection", "调整翻译行为、外观与服务连接"),
            ["InterfaceLanguage"] = ("Interface language", "界面语言"),
            ["GeneralTitle"] = ("General", "常规"),
            ["GeneralDescription"] = ("Control availability and selection behavior.", "控制应用状态与划词触发范围。"),
            ["NavigationGeneral"] = ("General", "常规"),
            ["NavigationAiHistory"] = ("AI history", "AI 记录"),
            ["NavigationTranslationAppearance"] = ("Translation & appearance", "翻译与外观"),
            ["NavigationModel"] = ("Model configuration", "模型配置"),
            ["SelectionTranslation"] = ("Selection translation", "划词翻译"),
            ["SelectionTranslationDescription"] = ("Translate text automatically after you select it.", "选中文字后自动读取并显示译文。"),
            ["StartWithWindows"] = ("Start with Windows", "开机启动"),
            ["StartWithWindowsDescription"] = ("Stay ready in the background after sign-in.", "登录 Windows 后在后台就绪。"),
            ["ClipboardFallback"] = ("Compatibility clipboard fallback", "兼容性剪贴板回退"),
            ["WpsPdfCompatibility"] = ("WPS PDF compatibility", "WPS PDF 兼容取词"),
            ["WpsPdfCompatibilityDescription"] = ("When WPS PDF exposes no selection, copy it and restore the clipboard where possible. Disable if copying is affected.", "WPS PDF 无法直接取词时自动复制，并尽量还原剪贴板；如影响复制可关闭。"),
            ["ClipboardFallbackDescription"] = ("For other apps without accessible text: try copying the selection and restore the clipboard where possible.", "用于其他无法直接取词的软件：尝试复制选区，并尽量还原剪贴板；仅在需要时开启。"),
            ["SelectionDelay"] = ("Trigger delay · ms", "触发等待 · 毫秒"),
            ["MaximumSelection"] = ("Selection limit · characters", "最大选区 · 字符"),
            ["TranslationAppearanceTitle"] = ("Translation & appearance", "翻译与外观"),
            ["TranslationAppearanceDescription"] = ("Choose the default language direction and reading experience.", "设置默认语言方向与浮窗阅读体验。"),
            ["SourceLanguage"] = ("Source language", "源语言"),
            ["TargetLanguage"] = ("Target language", "目标语言"),
            ["UseSelectionContext"] = ("Use surrounding context", "使用周围语境"),
            ["UseSelectionContextDescription"] = ("Use the surrounding paragraph to resolve ambiguity. Extra text is sent only when this is enabled.", "使用选区所在段落帮助消除歧义；只有开启后才会发送周围文字。"),
            ["TranslationMode"] = ("Translation mode", "翻译模式"),
            ["TranslationTone"] = ("Writing style", "表达风格"),
            ["PersonalGlossary"] = ("Personal glossary", "个人术语库"),
            ["PersonalGlossaryDescription"] = ("One term per line: source => preferred translation. Stored only on this device.", "每行一个术语：原词 => 指定译法。内容仅保存在本机。"),
            ["TranslationMemory"] = ("Saved translation memory", "已保存的翻译记忆"),
            ["TranslationMemoryDescription"] = ("Only corrections you save from the popup are encrypted for this Windows account. Up to 3 relevant pairs may guide future requests.", "仅保存你在浮窗中主动修正的译文，并为当前 Windows 账户加密；最多 3 组相关示例可辅助后续请求。"),
            ["ClearTranslationMemory"] = ("Clear", "清除"),
            ["TranslationMemoryEmpty"] = ("No saved corrections", "暂无已保存的修正"),
            ["TranslationMemoryCount"] = ("{0} encrypted correction(s) for this Windows account", "已为当前 Windows 账户加密保存 {0} 条修正"),
            ["ClearTranslationMemoryTitle"] = ("Clear translation memory?", "清除翻译记忆？"),
            ["ClearTranslationMemoryMessage"] = ("This permanently removes every source and corrected translation you explicitly saved.", "这会永久删除你主动保存的全部原文和修正译文。"),
            ["ClearTranslationMemoryFailed"] = ("Could not clear translation memory.", "无法清除翻译记忆。"),
            ["EnglishTranslationFont"] = ("English translation font", "英文译文字体"),
            ["ChineseTranslationFont"] = ("Chinese translation font", "中文译文字体"),
            ["AccentColor"] = ("Accent color", "强调色"),
            ["CustomColor"] = ("Custom color", "自定义颜色"),
            ["PopupVisualStyle"] = ("Popup style", "浮窗样式"),
            ["PopupStyleMinimal"] = ("Minimal", "极简"),
            ["PopupStyleBubble"] = ("Bubble", "气泡"),
            ["PopupStyleBubbleV2"] = ("Bubble 2.0", "气泡 2.0"),
            ["PopupStyleBubbleV3"] = ("Bubble 3.0 · Glass", "气泡 3.0 · 液态玻璃"),
            ["PopupStyleBubbleV3Color"] = ("Bubble 3.0 · Color Glass", "气泡 3.0 · 彩色玻璃"),
            ["PopupStyleGlassHint"] = ("See-through glass around a soft off-white, rounded text card.", "透明玻璃外层，正文使用略带灰蓝的雾白色圆角底板。"),
            ["PopupStyleColorGlassHint"] = ("Pink, blue and lilac glass around a softly tinted, rounded text card.", "保留粉、蓝、紫色染的透明外层，正文使用略带灰紫的雾白色圆角底板。"),
            ["HighlightPalette"] = ("Highlight palette", "重点高亮配色"),
            ["HighlightPalettePreview"] = ("AI emphasis preview", "AI 强调预览"),
            ["HighlightPaletteDescription"] = ("DeepSeek marks only the most useful generated phrases. Copy, history, and saved records keep normal text.", "DeepSeek 仅标注 AI 生成内容中最值得注意的短语；复制、记录和历史始终保留普通正文。"),
            ["HighlightPaletteClarity"] = ("Clarity", "清晰"),
            ["HighlightPaletteMorandi"] = ("Morandi", "莫兰迪"),
            ["HighlightPaletteOcean"] = ("Ocean", "海洋"),
            ["HighlightPaletteWarm"] = ("Warm", "暖调"),
            ["HighlightPaletteContrast"] = ("High contrast", "高对比"),
            ["DefaultTranslationFontSize"] = ("Default translation size", "默认译文字号"),
            ["AiHistoryTitle"] = ("AI history", "AI 记录"),
            ["AiHistoryDescription"] = ("Choose where manual Record entries are saved. Automatic history for every completed explanation and answer remains optional.", "选择手动 Record 内容的保存位置；是否自动保存每次完成的解释和问答仍由你决定。"),
            ["EnableAiHistory"] = ("Save AI explanations and Q&A", "保存 AI 解释和问答"),
            ["AiHistoryPrivacy"] = ("Manual Record saves only when clicked. Files are readable Markdown and may contain private selected text.", "手动 Record 只在点击后保存；文件是可直接阅读的 Markdown，可能包含私密划词内容。"),
            ["HistoryDirectory"] = ("Save folder", "保存目录"),
            ["ManualRecordDirectoryDescription"] = ("Manual Record works even when automatic history is off and appends to one Markdown file per day.", "即使关闭自动 AI 记录，手动 Record 仍会使用此目录，并按一天一份 Markdown 追加保存。"),
            ["Browse"] = ("Browse", "选择目录"),
            ["OpenFolder"] = ("Open", "打开目录"),
            ["SummaryRange"] = ("Summary range", "总结范围"),
            ["SummaryToday"] = ("Today", "今天"),
            ["SummaryLastSevenDays"] = ("Last 7 days", "最近 7 天"),
            ["SummaryAll"] = ("All records", "全部记录"),
            ["GenerateSummary"] = ("AI summary", "AI 总结"),
            ["HistoryDirectoryRequired"] = ("Choose a writable folder before enabling AI history.", "开启 AI 记录前，请选择可写入的目录。"),
            ["GeneratingSummary"] = ("Generating summary…", "正在生成总结…"),
            ["SummaryCreated"] = ("Summary created from {0} record(s).", "已根据 {0} 条记录生成总结。"),
            ["SummaryNoRecords"] = ("No Yita records were found in this range.", "所选范围内没有 Yita 记录。"),
            ["SummaryTimeout"] = ("Summary timed out. Please try again.", "总结超时，请重试。"),
            ["SummaryProviderFailed"] = ("The AI service could not generate the summary.", "AI 服务未能生成总结。"),
            ["HistoryDirectoryUnavailable"] = ("The selected folder is unavailable or not writable.", "所选目录不可用或无法写入。"),
            ["SummaryUnavailable"] = ("Save settings before generating a summary.", "请先保存设置，再生成总结。"),
            ["ChooseHistoryFolder"] = ("Choose an AI history folder", "选择 AI 记录目录"),
            ["TranslationServiceTitle"] = ("Translation service", "翻译服务"),
            ["TranslationServiceDescription"] = ("Your API key is stored only in Windows Credential Manager on this device.", "API Key 仅保存在本机 Windows 凭据管理器中。"),
            ["Provider"] = ("Provider", "服务"),
            ["Model"] = ("Model", "模型"),
            ["ApiEndpoint"] = ("API endpoint", "API Endpoint"),
            ["ApiKey"] = ("API key", "API Key"),
            ["TestConnection"] = ("Test connection", "测试连接"),
            ["ClearKey"] = ("Clear key", "清空密钥"),
            ["FooterHint"] = ("Changes take effect immediately after saving", "更改将在保存后立即生效"),
            ["Cancel"] = ("Cancel", "取消"),
            ["SaveSettings"] = ("Save settings", "保存设置"),
            ["InvalidSettings"] = ("Invalid settings", "设置无效"),
            ["DelayError"] = ("The selection delay must be an integer from 0 to 1000 milliseconds.", "选词等待时间必须是 0–1000 毫秒之间的整数。"),
            ["MaximumSelectionError"] = ("The selection limit must be an integer from 100 to 20,000 characters.", "最大选区字符数必须是 100–20000 之间的整数。"),
            ["LanguageEmptyError"] = ("Source and target languages cannot be empty.", "源语言和目标语言不能为空。"),
            ["CustomColorError"] = ("The custom color must use #RRGGBB format, for example #24756B.", "自定义颜色必须是 #RRGGBB 格式，例如 #24756B。"),
            ["EndpointError"] = ("A remote DeepSeek endpoint must use HTTPS. HTTP is allowed only for local addresses.", "DeepSeek 远程 Endpoint 必须使用 HTTPS；仅本机地址允许 HTTP。"),
            ["ModelEmptyError"] = ("The DeepSeek model cannot be empty.", "DeepSeek Model 不能为空。"),
            ["ApiKeyEmptyError"] = ("Enter an API key when using the DeepSeek provider.", "请选择 DeepSeek Provider 后填写 API Key。"),
            ["FillConfiguration"] = ("Complete the configuration first", "请先填写完整配置"),
            ["Connecting"] = ("Connecting…", "正在连接…"),
            ["ConnectionSuccess"] = ("✓ Connected", "✓ 连接成功"),
            ["ConnectionTimeout"] = ("Connection timed out", "连接超时"),
            ["DeleteAfterSave"] = ("Credential will be deleted after saving", "保存后删除凭据"),
            ["NoTestTranslation"] = ("The service returned no test translation.", "服务未返回测试译文。"),
            ["SelectionDelayTooltip"] = ("Prevents accidental triggers from window dragging or brief selections; range 0–1000", "避免拖动窗口或短暂选择造成误触；范围 0–1000"),
            ["MaximumSelectionTooltip"] = ("Prevents accidental translation of an entire document; range 100–20000", "用于防止误选整篇文档；范围 100–20000"),
            ["CustomColorTooltip"] = ("Enter #RRGGBB, for example #24756B", "输入 #RRGGBB，例如 #24756B"),
        };

    private string _uiLanguage = "en";
    private string? _connectionStatusLocalizationKey;
    private readonly Func<int>? _getTranslationMemoryCount;
    private readonly Action? _clearTranslationMemory;
    private readonly AppSettings _baseSettings;
    private readonly Func<AppSettings, SummaryRange, CancellationToken, Task<AiSummaryGenerationResult>>? _generateSummary;
    private CancellationTokenSource? _connectionTestCancellation;
    private CancellationTokenSource? _summaryCancellation;
    private bool _isClosed;
    private bool _apiKeyClearRequested;
    private WpfListBox? _settingsNavigation;
    private WpfContentControl? _settingsPageHost;
    private readonly Dictionary<string, UIElement> _settingsPages = new(StringComparer.Ordinal);

    public SettingsWindow(
        AppSettings settings,
        Func<int>? getTranslationMemoryCount = null,
        Action? clearTranslationMemory = null,
        Func<AppSettings, SummaryRange, CancellationToken, Task<AiSummaryGenerationResult>>? generateSummary = null)
    {
        _baseSettings = settings;
        _getTranslationMemoryCount = getTranslationMemoryCount;
        _clearTranslationMemory = clearTranslationMemory;
        _generateSummary = generateSummary;
        InitializeComponent();
        BuildSettingsNavigation();

        EnabledCheckBox.IsChecked = settings.IsEnabled;
        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
        ClipboardFallbackCheckBox.IsChecked = settings.UseClipboardFallback;
        WpsPdfCompatibilityCheckBox.IsChecked = settings.UseWpsPdfCompatibility;
        SelectionDelayTextBox.Text = settings.SelectionDelayMilliseconds.ToString();
        MaximumSelectionTextBox.Text = settings.MaximumSelectionCharacters.ToString();
        UseSelectionContextCheckBox.IsChecked = settings.UseSelectionContext;
        SetComboText(SourceLanguageComboBox, settings.SourceLanguage);
        SetComboText(
            TargetLanguageComboBox,
            settings.TargetLanguageMode == "auto" ? "自动判断" : settings.TargetLanguage);
        SetComboText(
            TranslationModeComboBox,
            TranslationPreferenceCatalog.NormalizeMode(settings.TranslationMode));
        SetComboText(
            TranslationToneComboBox,
            TranslationPreferenceCatalog.NormalizeTone(settings.TranslationTone));
        PersonalGlossaryTextBox.Text = settings.PersonalGlossary;
        SelectTheme(settings.ColorTheme);
        CustomAccentColorTextBox.Text = settings.CustomAccentColor;
        SelectPopupVisualStyle(settings.PopupVisualStyle);
        SelectHighlightPalette(settings.HighlightPalette);
        DefaultFontSizeSlider.Value = Math.Clamp(settings.DefaultTranslationFontSize, 12, 34);
        SetComboText(
            EnglishTranslationFontComboBox,
            TranslationFontCatalog.NormalizeEnglish(settings.EnglishTranslationFontFamily));
        SetComboText(
            ChineseTranslationFontComboBox,
            TranslationFontCatalog.NormalizeChinese(settings.ChineseTranslationFontFamily));
        SelectProvider(settings.ProviderId);
        EndpointTextBox.Text = settings.DeepSeekEndpoint;
        SetComboText(ModelComboBox, settings.DeepSeekModel);
        ApiKeyPasswordBox.Password = settings.DeepSeekApiKey;
        AiHistoryEnabledCheckBox.IsChecked = settings.AiHistoryEnabled;
        AiHistoryDirectoryTextBox.Text = settings.AiHistoryDirectory;
        SelectSummaryRange(settings.AiSummaryRange);
        ApplyUiLanguage(settings.UiLanguage);
        UpdateProviderFields();
        UpdateThemePreview();
        UpdateTranslationMemoryStatus();
        UpdateAiHistoryControls();
    }

    public AppSettings? ResultSettings { get; private set; }

    public bool ApiKeyClearRequested => _apiKeyClearRequested;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        UiMotion.Reveal(SettingsRoot);
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _connectionTestCancellation?.Cancel();
        _summaryCancellation?.Cancel();
        ApiKeyPasswordBox.Clear();
        DataContext = null;
        Content = null;
        Resources.Clear();
        base.OnClosed(e);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SelectionDelayTextBox.Text, out var delay) || delay is < 0 or > 1000)
        {
            WpfMessageBox.Show(this, L("DelayError"), L("InvalidSettings"), MessageBoxButton.OK, MessageBoxImage.Warning);
            SelectionDelayTextBox.Focus();
            return;
        }

        if (!int.TryParse(MaximumSelectionTextBox.Text, out var maximumSelectionCharacters)
            || maximumSelectionCharacters is < 100 or > 20000)
        {
            WpfMessageBox.Show(this, L("MaximumSelectionError"), L("InvalidSettings"), MessageBoxButton.OK, MessageBoxImage.Warning);
            MaximumSelectionTextBox.Focus();
            return;
        }

        var sourceLanguage = ReadComboText(SourceLanguageComboBox);
        var targetSelection = ReadComboText(TargetLanguageComboBox);
        if (string.IsNullOrWhiteSpace(sourceLanguage) || string.IsNullOrWhiteSpace(targetSelection))
        {
            WpfMessageBox.Show(this, L("LanguageEmptyError"), L("InvalidSettings"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var providerId = ProviderComboBox.SelectedValue as string ?? "deepseek";
        var colorTheme = ColorThemeComboBox.SelectedValue as string ?? ThemeCatalog.DefaultThemeId;
        var popupVisualStyle = PopupVisualStyleCatalog.Normalize(
            PopupVisualStyleComboBox.SelectedValue as string);
        var highlightPalette = HighlightPaletteCatalog.Normalize(
            HighlightPaletteComboBox.SelectedValue as string);
        var customAccentColor = CustomAccentColorTextBox.Text.Trim();
        if (colorTheme == ThemeCatalog.CustomThemeId
            && !ThemeCatalog.TryNormalizeHexColor(customAccentColor, out customAccentColor))
        {
            WpfMessageBox.Show(this, L("CustomColorError"), L("InvalidSettings"), MessageBoxButton.OK, MessageBoxImage.Warning);
            CustomAccentColorTextBox.Focus();
            return;
        }

        var endpointText = EndpointTextBox.Text.Trim();
        var model = ReadComboText(ModelComboBox);
        var apiKey = ApiKeyPasswordBox.Password.Trim();
        if (providerId == "deepseek")
        {
            if (!TranslationProviderFactory.TryValidateEndpoint(endpointText, out var endpoint))
            {
                WpfMessageBox.Show(this, L("EndpointError"), L("InvalidSettings"), MessageBoxButton.OK, MessageBoxImage.Warning);
                EndpointTextBox.Focus();
                return;
            }

            endpointText = endpoint!.ToString().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(model))
            {
                WpfMessageBox.Show(this, L("ModelEmptyError"), L("InvalidSettings"), MessageBoxButton.OK, MessageBoxImage.Warning);
                ModelComboBox.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(apiKey) && !_apiKeyClearRequested)
            {
                WpfMessageBox.Show(this, L("ApiKeyEmptyError"), L("InvalidSettings"), MessageBoxButton.OK, MessageBoxImage.Warning);
                ApiKeyPasswordBox.Focus();
                return;
            }
        }

        var aiHistoryEnabled = AiHistoryEnabledCheckBox.IsChecked == true;
        var aiHistoryDirectory = AiHistoryDirectoryTextBox.Text.Trim();
        if (aiHistoryEnabled && string.IsNullOrWhiteSpace(aiHistoryDirectory))
        {
            WpfMessageBox.Show(this, L("HistoryDirectoryRequired"), L("InvalidSettings"), MessageBoxButton.OK, MessageBoxImage.Warning);
            AiHistoryDirectoryTextBox.Focus();
            return;
        }

        if (!string.IsNullOrWhiteSpace(aiHistoryDirectory)
            && (!Path.IsPathFullyQualified(aiHistoryDirectory)
                || !AiHistoryStore.TryValidateWritableDirectory(aiHistoryDirectory, out _)))
        {
            WpfMessageBox.Show(this, L("HistoryDirectoryUnavailable"), L("InvalidSettings"), MessageBoxButton.OK, MessageBoxImage.Warning);
            AiHistoryDirectoryTextBox.Focus();
            return;
        }

        ResultSettings = new AppSettings
        {
            TypographyVersion = 1,
            BrandPaletteVersion = 1,
            IsEnabled = EnabledCheckBox.IsChecked == true,
            StartWithWindows = StartWithWindowsCheckBox.IsChecked == true,
            UseClipboardFallback = ClipboardFallbackCheckBox.IsChecked == true,
            UseWpsPdfCompatibility = WpsPdfCompatibilityCheckBox.IsChecked == true,
            SelectionDelayMilliseconds = delay,
            MaximumSelectionCharacters = maximumSelectionCharacters,
            SourceLanguage = sourceLanguage,
            TargetLanguageMode = targetSelection == "自动判断" ? "auto" : "fixed",
            TargetLanguage = targetSelection == "自动判断" ? "简体中文" : targetSelection,
            UseSelectionContext = UseSelectionContextCheckBox.IsChecked == true,
            TranslationMode = TranslationPreferenceCatalog.NormalizeMode(
                ReadComboText(TranslationModeComboBox)),
            TranslationTone = TranslationPreferenceCatalog.NormalizeTone(
                ReadComboText(TranslationToneComboBox)),
            PersonalGlossary = PersonalGlossaryTextBox.Text.Trim(),
            ColorTheme = colorTheme,
            CustomAccentColor = customAccentColor,
            PopupVisualStyle = popupVisualStyle,
            HighlightPalette = highlightPalette,
            DefaultTranslationFontSize = Math.Round(DefaultFontSizeSlider.Value, 1),
            UiLanguage = _uiLanguage,
            EnglishTranslationFontFamily = TranslationFontCatalog.NormalizeEnglish(ReadComboText(EnglishTranslationFontComboBox)),
            ChineseTranslationFontFamily = TranslationFontCatalog.NormalizeChinese(ReadComboText(ChineseTranslationFontComboBox)),
            ProviderId = providerId,
            DeepSeekEndpoint = endpointText,
            DeepSeekModel = model,
            DeepSeekApiKey = apiKey,
            AiHistoryEnabled = aiHistoryEnabled,
            AiHistoryDirectory = aiHistoryDirectory,
            AiSummaryRange = ReadSummaryRange(),
        };

        DialogResult = true;
    }

    private static string ReadComboText(System.Windows.Controls.ComboBox comboBox)
    {
        if (comboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item
            && item.Tag is string stableValue
            && !string.IsNullOrWhiteSpace(stableValue))
        {
            return stableValue.Trim();
        }

        return comboBox.Text.Trim();
    }

    private static void SetComboText(System.Windows.Controls.ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        if (comboBox.IsEditable)
        {
            comboBox.Text = value;
        }
        else
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private void UiLanguageButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyUiLanguage(
            _uiLanguage == UiLanguageCatalog.SimplifiedChineseLanguageId
                ? UiLanguageCatalog.EnglishLanguageId
                : UiLanguageCatalog.SimplifiedChineseLanguageId);
    }

    private void ApplyUiLanguage(string? language)
    {
        _uiLanguage = NormalizeUiLanguage(language);
        UpdateSettingsNavigationLabels();
        UiLanguageButton.Content = _uiLanguage == UiLanguageCatalog.SimplifiedChineseLanguageId
            ? "English"
            : "中文";
        Title = L("WindowTitle");
        ApplyLocalizedContent(this);

        System.Windows.Automation.AutomationProperties.SetName(
            EnabledCheckBox,
            L("SelectionTranslation"));
        System.Windows.Automation.AutomationProperties.SetName(
            StartWithWindowsCheckBox,
            L("StartWithWindows"));
        System.Windows.Automation.AutomationProperties.SetName(
            ClipboardFallbackCheckBox,
            L("ClipboardFallback"));
        System.Windows.Automation.AutomationProperties.SetName(
            WpsPdfCompatibilityCheckBox,
            L("WpsPdfCompatibility"));
        System.Windows.Automation.AutomationProperties.SetName(
            UseSelectionContextCheckBox,
            L("UseSelectionContext"));
        System.Windows.Automation.AutomationProperties.SetName(
            PersonalGlossaryTextBox,
            L("PersonalGlossary"));
        System.Windows.Automation.AutomationProperties.SetName(
            ClearTranslationMemoryButton,
            L("ClearTranslationMemory"));
        System.Windows.Automation.AutomationProperties.SetName(
            PopupVisualStyleComboBox,
            L("PopupVisualStyle"));
        System.Windows.Automation.AutomationProperties.SetName(
            HighlightPaletteComboBox,
            L("HighlightPalette"));

        SelectionDelayTextBox.ToolTip = L("SelectionDelayTooltip");
        MaximumSelectionTextBox.ToolTip = L("MaximumSelectionTooltip");
        CustomAccentColorTextBox.ToolTip = L("CustomColorTooltip");

        SetComboItemContent(SourceLanguageComboBox, "自动检测", _uiLanguage == "zh-CN" ? "自动检测" : "Auto detect");
        SetComboItemContent(SourceLanguageComboBox, "英语", _uiLanguage == "zh-CN" ? "英语" : "English");
        SetComboItemContent(SourceLanguageComboBox, "简体中文", _uiLanguage == "zh-CN" ? "简体中文" : "Simplified Chinese");

        SetComboItemContent(TargetLanguageComboBox, "自动判断", _uiLanguage == "zh-CN" ? "自动判断" : "Choose automatically");
        SetComboItemContent(TargetLanguageComboBox, "简体中文", _uiLanguage == "zh-CN" ? "简体中文" : "Simplified Chinese");
        SetComboItemContent(TargetLanguageComboBox, "英语", _uiLanguage == "zh-CN" ? "英语" : "English");
        SetComboItemContent(TargetLanguageComboBox, "日语", _uiLanguage == "zh-CN" ? "日语" : "Japanese");

        SetComboItemContent(TranslationModeComboBox, "fast", _uiLanguage == "zh-CN" ? "快速" : "Fast");
        SetComboItemContent(TranslationModeComboBox, "balanced", _uiLanguage == "zh-CN" ? "均衡" : "Balanced");
        SetComboItemContent(TranslationModeComboBox, "precise", _uiLanguage == "zh-CN" ? "精确" : "Precise");

        SetComboItemContent(TranslationToneComboBox, "natural", _uiLanguage == "zh-CN" ? "自然" : "Natural");
        SetComboItemContent(TranslationToneComboBox, "formal", _uiLanguage == "zh-CN" ? "正式" : "Formal");
        SetComboItemContent(TranslationToneComboBox, "concise", _uiLanguage == "zh-CN" ? "简洁" : "Concise");
        SetComboItemContent(TranslationToneComboBox, "academic", _uiLanguage == "zh-CN" ? "学术" : "Academic");
        SetComboItemContent(TranslationToneComboBox, "technical", _uiLanguage == "zh-CN" ? "技术" : "Technical");

        SetComboItemContent(ColorThemeComboBox, "yita", _uiLanguage == "zh-CN" ? "译獭 · 湖畔" : "Yita · Lakeside");
        SetComboItemContent(HighlightPaletteComboBox, "yita", _uiLanguage == "zh-CN" ? "译獭 · 柔和强调" : "Yita · Soft emphasis");
        SetComboItemContent(ColorThemeComboBox, "ocean", _uiLanguage == "zh-CN" ? "深海蓝" : "Ocean blue");
        SetComboItemContent(ColorThemeComboBox, "violet", _uiLanguage == "zh-CN" ? "紫罗兰" : "Violet");
        SetComboItemContent(ColorThemeComboBox, "emerald", _uiLanguage == "zh-CN" ? "翡翠绿" : "Emerald");
        SetComboItemContent(ColorThemeComboBox, "sunset", _uiLanguage == "zh-CN" ? "暖橙色" : "Warm orange");
        SetComboItemContent(ColorThemeComboBox, "rose", _uiLanguage == "zh-CN" ? "玫瑰红" : "Rose");
        SetComboItemContent(ColorThemeComboBox, "custom", _uiLanguage == "zh-CN" ? "自定义" : "Custom");
        SetComboItemContent(PopupVisualStyleComboBox, "minimal", L("PopupStyleMinimal"));
        SetComboItemContent(PopupVisualStyleComboBox, "bubble", L("PopupStyleBubble"));
        SetComboItemContent(PopupVisualStyleComboBox, "bubble-v2", L("PopupStyleBubbleV2"));
        SetComboItemContent(PopupVisualStyleComboBox, "bubble-v3", L("PopupStyleBubbleV3"));
        SetComboItemContent(PopupVisualStyleComboBox, "bubble-v3-color", L("PopupStyleBubbleV3Color"));
        SetComboItemContent(HighlightPaletteComboBox, "clarity", L("HighlightPaletteClarity"));
        SetComboItemContent(HighlightPaletteComboBox, "morandi", L("HighlightPaletteMorandi"));
        SetComboItemContent(HighlightPaletteComboBox, "ocean", L("HighlightPaletteOcean"));
        SetComboItemContent(HighlightPaletteComboBox, "warm", L("HighlightPaletteWarm"));
        SetComboItemContent(HighlightPaletteComboBox, "contrast", L("HighlightPaletteContrast"));

        SetComboItemContent(ProviderComboBox, "deepseek", "DeepSeek API");
        SetComboItemContent(ProviderComboBox, "mock", _uiLanguage == "zh-CN" ? "Mock · 离线测试" : "Mock · offline test");
        SetComboItemContent(AiSummaryRangeComboBox, "today", L("SummaryToday"));
        SetComboItemContent(AiSummaryRangeComboBox, "last-7-days", L("SummaryLastSevenDays"));
        SetComboItemContent(AiSummaryRangeComboBox, "all", L("SummaryAll"));

        SetComboItemContent(ChineseTranslationFontComboBox, "SimHei", _uiLanguage == UiLanguageCatalog.SimplifiedChineseLanguageId ? "黑体 (SimHei)" : "SimHei (Heiti)");
        SetComboItemContent(ChineseTranslationFontComboBox, "Microsoft YaHei UI", _uiLanguage == UiLanguageCatalog.SimplifiedChineseLanguageId ? "微软雅黑 (Microsoft YaHei UI)" : "Microsoft YaHei UI");
        SetComboItemContent(ChineseTranslationFontComboBox, "SimSun", _uiLanguage == UiLanguageCatalog.SimplifiedChineseLanguageId ? "宋体 (SimSun)" : "SimSun (Songti)");
        SetComboItemContent(ChineseTranslationFontComboBox, "KaiTi", _uiLanguage == UiLanguageCatalog.SimplifiedChineseLanguageId ? "楷体 (KaiTi)" : "KaiTi");

        if (_connectionStatusLocalizationKey is not null)
        {
            ConnectionStatusText.Text = L(_connectionStatusLocalizationKey);
        }

        UpdateTranslationMemoryStatus();
        UpdateAiHistoryControls();
    }

    private void BuildSettingsNavigation()
    {
        if (SettingsScrollViewer.Content is not StackPanel source)
        {
            return;
        }

        var cards = source.Children.OfType<UIElement>().ToArray();
        if (cards.Length < 4)
        {
            return;
        }

        source.Children.Clear();
        var categories = new[] { "general", "ai-history", "translation-appearance", "model" };
        for (var index = 0; index < categories.Length; index++)
        {
            var page = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            page.Children.Add(cards[index]);
            _settingsPages[categories[index]] = page;
        }

        var shell = new Grid { MinWidth = 680 };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(178) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var navigationSurface = new Border
        {
            Margin = new Thickness(0, 0, 18, 0),
            Padding = new Thickness(7),
            Background = (WpfBrush)FindResource("AppCardBackgroundBrush"),
            CornerRadius = new CornerRadius(16),
            VerticalAlignment = VerticalAlignment.Top,
        };
        _settingsNavigation = new WpfListBox
        {
            BorderThickness = new Thickness(0),
            Background = WpfBrushes.Transparent,
            ItemContainerStyle = (Style)FindResource("SettingsNavigationItemStyle"),
            SelectionMode = System.Windows.Controls.SelectionMode.Single,
        };
        foreach (var category in categories)
        {
            _settingsNavigation.Items.Add(new WpfListBoxItem { Tag = category });
        }
        _settingsNavigation.SelectionChanged += SettingsNavigation_SelectionChanged;
        navigationSurface.Child = _settingsNavigation;
        shell.Children.Add(navigationSurface);

        _settingsPageHost = new WpfContentControl
        {
            Margin = new Thickness(0),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Top,
        };
        Grid.SetColumn(_settingsPageHost, 1);
        shell.Children.Add(_settingsPageHost);
        SettingsScrollViewer.Content = shell;
        _settingsNavigation.SelectedIndex = 0;
    }

    private void SettingsNavigation_SelectionChanged(object sender, WpfSelectionChangedEventArgs e)
    {
        if (_settingsNavigation?.SelectedItem is not WpfListBoxItem item
            || item.Tag is not string category
            || _settingsPageHost is null
            || !_settingsPages.TryGetValue(category, out var page))
        {
            return;
        }

        _settingsPageHost.Content = page;
        SettingsScrollViewer.ScrollToTop();
    }

    private void UpdateSettingsNavigationLabels()
    {
        if (_settingsNavigation is null)
        {
            return;
        }

        foreach (var item in _settingsNavigation.Items.OfType<WpfListBoxItem>())
        {
            item.Content = (item.Tag as string) switch
            {
                "general" => L("NavigationGeneral"),
                "ai-history" => L("NavigationAiHistory"),
                "translation-appearance" => L("NavigationTranslationAppearance"),
                "model" => L("NavigationModel"),
                _ => item.Tag?.ToString() ?? string.Empty,
            };
        }
    }

    private void SelectSummaryRange(SummaryRange range)
    {
        var stableId = SummaryRangeCatalog.ToStableId(range);
        foreach (var item in AiSummaryRangeComboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, stableId, StringComparison.OrdinalIgnoreCase))
            {
                AiSummaryRangeComboBox.SelectedItem = item;
                return;
            }
        }

        AiSummaryRangeComboBox.SelectedIndex = 0;
    }

    private SummaryRange ReadSummaryRange() =>
        SummaryRangeCatalog.FromStableId(AiSummaryRangeComboBox.SelectedValue as string);

    private void AiHistoryEnabledCheckBox_Changed(object sender, RoutedEventArgs e) =>
        UpdateAiHistoryControls();

    private void AiHistoryDirectoryTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateAiHistoryControls();

    private void UpdateAiHistoryControls()
    {
        if (AiHistoryDirectoryTextBox is null || GenerateAiSummaryButton is null)
        {
            return;
        }

        var hasDirectory = Path.IsPathFullyQualified(AiHistoryDirectoryTextBox.Text.Trim());
        OpenAiHistoryDirectoryButton.IsEnabled = hasDirectory;
        GenerateAiSummaryButton.IsEnabled = AiHistoryEnabledCheckBox.IsChecked == true
                                            && hasDirectory
                                            && _generateSummary is not null
                                            && _summaryCancellation is null;
    }

    private void BrowseAiHistoryDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = L("ChooseHistoryFolder"),
            Multiselect = false,
        };
        if (Path.IsPathFullyQualified(AiHistoryDirectoryTextBox.Text.Trim()))
        {
            dialog.InitialDirectory = AiHistoryDirectoryTextBox.Text.Trim();
        }

        if (dialog.ShowDialog(this) == true)
        {
            AiHistoryDirectoryTextBox.Text = dialog.FolderName;
            AiHistoryStatusText.Text = string.Empty;
        }
    }

    private void OpenAiHistoryDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var directory = AiHistoryDirectoryTextBox.Text.Trim();
        if (!Path.IsPathFullyQualified(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            var recordsRoot = Path.Combine(directory, "Yita Records");
            Process.Start(new ProcessStartInfo(Directory.Exists(recordsRoot) ? recordsRoot : directory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AiHistoryStatusText.Text = L("HistoryDirectoryUnavailable");
            AiHistoryStatusText.SetResourceReference(ForegroundProperty, "DangerBrush");
        }
    }

    private async void GenerateAiSummaryButton_Click(object sender, RoutedEventArgs e)
    {
        var directory = AiHistoryDirectoryTextBox.Text.Trim();
        if (_generateSummary is null
            || AiHistoryEnabledCheckBox.IsChecked != true
            || !Path.IsPathFullyQualified(directory)
            || !AiHistoryStore.TryValidateWritableDirectory(directory, out _))
        {
            AiHistoryStatusText.Text = L("HistoryDirectoryRequired");
            AiHistoryStatusText.SetResourceReference(ForegroundProperty, "DangerBrush");
            return;
        }

        _summaryCancellation?.Cancel();
        _summaryCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _summaryCancellation = cancellation;
        UpdateAiHistoryControls();
        AiHistoryStatusText.Text = L("GeneratingSummary");
        AiHistoryStatusText.SetResourceReference(ForegroundProperty, "AppMutedTextBrush");
        try
        {
            var settings = _baseSettings with
            {
                AiHistoryEnabled = true,
                AiHistoryDirectory = directory,
                AiSummaryRange = ReadSummaryRange(),
                UiLanguage = _uiLanguage,
                ProviderId = ProviderComboBox.SelectedValue as string ?? "deepseek",
                DeepSeekEndpoint = EndpointTextBox.Text.Trim(),
                DeepSeekModel = ReadComboText(ModelComboBox),
                DeepSeekApiKey = ApiKeyPasswordBox.Password.Trim(),
            };
            var result = await _generateSummary(settings, settings.AiSummaryRange, cancellation.Token);
            if (_isClosed || cancellation.IsCancellationRequested)
            {
                return;
            }

            AiHistoryStatusText.Text = result.Succeeded
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture, L("SummaryCreated"), result.RecordCount)
                : L(result.ErrorCode switch
                {
                    "no_records" => "SummaryNoRecords",
                    "timeout" => "SummaryTimeout",
                    "provider_failed" => "SummaryProviderFailed",
                    "directory_unavailable" => "HistoryDirectoryUnavailable",
                    _ => "SummaryUnavailable",
                });
            AiHistoryStatusText.SetResourceReference(
                ForegroundProperty,
                result.Succeeded ? "SuccessBrush" : "DangerBrush");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_summaryCancellation, cancellation))
            {
                _summaryCancellation = null;
            }

            cancellation.Dispose();
            if (!_isClosed)
            {
                UpdateAiHistoryControls();
            }
        }
    }

    private void ClearTranslationMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_clearTranslationMemory is null || (_getTranslationMemoryCount?.Invoke() ?? 0) == 0)
        {
            return;
        }

        var result = WpfMessageBox.Show(
            this,
            L("ClearTranslationMemoryMessage"),
            L("ClearTranslationMemoryTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _clearTranslationMemory();
            UpdateTranslationMemoryStatus();
        }
        catch (Exception exception) when (exception is System.IO.IOException
            or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Yita could not clear translation memory: {exception}");
            WpfMessageBox.Show(
                this,
                L("ClearTranslationMemoryFailed"),
                L("ClearTranslationMemoryTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void UpdateTranslationMemoryStatus()
    {
        if (TranslationMemoryStatusText is null || ClearTranslationMemoryButton is null)
        {
            return;
        }

        var count = _getTranslationMemoryCount?.Invoke() ?? 0;
        TranslationMemoryStatusText.Text = count == 0
            ? L("TranslationMemoryEmpty")
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, L("TranslationMemoryCount"), count);
        ClearTranslationMemoryButton.IsEnabled = count > 0 && _clearTranslationMemory is not null;
    }

    private void ApplyLocalizedContent(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dependencyObject)
            {
                continue;
            }

            if (dependencyObject is FrameworkElement element
                && element.Tag is string tag
                && tag.StartsWith("loc:", StringComparison.Ordinal))
            {
                var localized = L(tag[4..]);
                switch (element)
                {
                    case System.Windows.Controls.TextBlock textBlock:
                        textBlock.Text = localized;
                        break;
                    case System.Windows.Controls.ContentControl contentControl:
                        contentControl.Content = localized;
                        break;
                }
            }

            ApplyLocalizedContent(dependencyObject);
        }
    }

    private static void SetComboItemContent(
        System.Windows.Controls.ComboBox comboBox,
        string stableValue,
        string displayText)
    {
        foreach (var item in comboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, stableValue, StringComparison.OrdinalIgnoreCase))
            {
                item.Content = displayText;
                return;
            }
        }
    }

    private string L(string key)
    {
        if (!LocalizedText.TryGetValue(key, out var text))
        {
            return key;
        }

        return _uiLanguage == UiLanguageCatalog.SimplifiedChineseLanguageId ? text.Chinese : text.English;
    }

    private static string NormalizeUiLanguage(string? language)
    {
        return UiLanguageCatalog.Normalize(language);
    }

    private void ProviderComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsInitialized)
        {
            UpdateProviderFields();
        }
    }

    private void SelectProvider(string providerId)
    {
        foreach (var item in ProviderComboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, providerId, StringComparison.OrdinalIgnoreCase))
            {
                ProviderComboBox.SelectedItem = item;
                return;
            }
        }

        ProviderComboBox.SelectedIndex = 0;
    }

    private void SelectTheme(string themeId)
    {
        foreach (var item in ColorThemeComboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, themeId, StringComparison.OrdinalIgnoreCase))
            {
                ColorThemeComboBox.SelectedItem = item;
                return;
            }
        }

        ColorThemeComboBox.SelectedIndex = 0;
    }

    private void SelectPopupVisualStyle(string popupVisualStyle)
    {
        var normalizedStyle = PopupVisualStyleCatalog.Normalize(popupVisualStyle);
        foreach (var item in PopupVisualStyleComboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, normalizedStyle, StringComparison.OrdinalIgnoreCase))
            {
                PopupVisualStyleComboBox.SelectedItem = item;
                return;
            }
        }

        PopupVisualStyleComboBox.SelectedIndex = 0;
    }

    private void SelectHighlightPalette(string highlightPalette)
    {
        var normalizedPalette = HighlightPaletteCatalog.Normalize(highlightPalette);
        foreach (var item in HighlightPaletteComboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, normalizedPalette, StringComparison.OrdinalIgnoreCase))
            {
                HighlightPaletteComboBox.SelectedItem = item;
                return;
            }
        }

        HighlightPaletteComboBox.SelectedIndex = 0;
    }

    private void ColorThemeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsInitialized)
        {
            UpdateThemePreview();
        }
    }

    private void PopupVisualStyleComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsInitialized)
        {
            UpdatePopupStylePreview();
        }
    }

    private void HighlightPaletteComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsInitialized)
        {
            UpdateHighlightPalettePreview();
        }
    }

    private void CustomAccentColorTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (IsInitialized)
        {
            UpdateThemePreview();
        }
    }

    private void UpdateThemePreview()
    {
        var themeId = ColorThemeComboBox.SelectedValue as string ?? ThemeCatalog.DefaultThemeId;
        CustomAccentColorTextBox.IsEnabled = themeId == ThemeCatalog.CustomThemeId;
        var palette = ThemeCatalog.Resolve(themeId, CustomAccentColorTextBox.Text);
        ThemePreviewBorder.Background = ThemeManager.CreateBrush(palette.Accent);
        UpdateHighlightPalettePreview();
        UpdatePopupStylePreview();
    }

    private void UpdateHighlightPalettePreview()
    {
        if (HighlightPrimaryPreviewBorder is null
            || HighlightSecondaryPreviewBorder is null
            || HighlightTertiaryPreviewBorder is null)
        {
            return;
        }

        var palette = HighlightPaletteCatalog.Resolve(
            HighlightPaletteComboBox.SelectedValue as string);
        HighlightPrimaryPreviewBorder.Background = ThemeManager.CreateBrush(palette.PrimaryBackground);
        HighlightSecondaryPreviewBorder.Background = ThemeManager.CreateBrush(palette.SecondaryBackground);
        HighlightTertiaryPreviewBorder.Background = ThemeManager.CreateBrush(palette.TertiaryBackground);
        HighlightPrimaryPreviewText.Foreground = ThemeManager.CreateBrush(palette.PrimaryForeground);
        HighlightSecondaryPreviewText.Foreground = ThemeManager.CreateBrush(palette.SecondaryForeground);
        HighlightTertiaryPreviewText.Foreground = ThemeManager.CreateBrush(palette.TertiaryForeground);
    }

    private void UpdatePopupStylePreview()
    {
        if (PopupStylePreviewSurface is null || PopupStylePreviewTail is null)
        {
            return;
        }

        var popupVisualStyle = PopupVisualStyleCatalog.Normalize(
            PopupVisualStyleComboBox.SelectedValue as string);
        var isBubble = PopupVisualStyleCatalog.IsBubble(popupVisualStyle);
        var isBubbleV2 = PopupVisualStyleCatalog.IsBubbleV2(popupVisualStyle);
        var isGlass = PopupVisualStyleCatalog.IsBubbleV3(popupVisualStyle);
        var isColorGlass = PopupVisualStyleCatalog.IsBubbleV3Color(popupVisualStyle);
        var isSculpted = isBubbleV2 || isGlass;
        var themeId = ColorThemeComboBox.SelectedValue as string ?? ThemeCatalog.DefaultThemeId;
        var palette = ThemeCatalog.Resolve(themeId, CustomAccentColorTextBox.Text);
        var tailColor = isColorGlass ? "#50F2F4FF" : isGlass ? "#50F1F7FC" : isBubbleV2
            ? "#F2F4FF"
            : isBubble
                ? "#EEF7FF"
                : palette.PopupBackground;
        if (YitaPalette.IsActive(palette))
            tailColor = isGlass ? "#60FFF8EC" : palette.PopupBackground;

        PopupStylePreviewSurface.Margin = isBubble
            ? new Thickness(isSculpted ? 14 : 12, 2, 0, 2)
            : new Thickness(0, 4, 0, 4);
        PopupStylePreviewSurface.CornerRadius = new CornerRadius(isSculpted ? 22 : isBubble ? 18 : 7);
        PopupStylePreviewSurface.BorderThickness = new Thickness(isGlass ? 1 : isBubble ? 0 : 1);
        PopupStylePreviewSurface.Background = ThemeManager.CreatePopupPreviewSurfaceBrush(
            popupVisualStyle,
            palette);
        PopupStylePreviewTextSurface.Background = isGlass && !SystemParameters.HighContrast
            ? YitaPalette.IsActive(palette) ? ThemeManager.CreateBrush(palette.PopupBackground)
                : isColorGlass ? LiquidGlassMaterial.ColorTextSurface : LiquidGlassMaterial.TextSurface
            : System.Windows.Media.Brushes.Transparent;
        PopupStylePreviewSurface.BorderBrush = isGlass ? LiquidGlassMaterial.Edge : ThemeManager.CreateBrush(
            isBubbleV2 ? "#FFFFFF" : isBubble ? "#C9D9EA" : palette.PopupBorder);
        PopupStylePreviewTail.Data = System.Windows.Media.Geometry.Parse(isSculpted
            ? "M0,6 C3,4 6,1.5 12,0 C10,3.8 10,8.2 12,12 C6,10.5 3,8 0,6 Z"
            : "M0,0 L12,6 L0,12 Z");
        PopupStylePreviewTail.Fill = ThemeManager.CreateBrush(tailColor);
        PopupStylePreviewTail.Stroke = ThemeManager.CreateBrush(isSculpted ? "#FFFFFF" : "#00FFFFFF");
        PopupStylePreviewTail.StrokeThickness = isSculpted ? 0.8 : 0;
        PopupStylePreviewTail.Visibility = isBubble ? Visibility.Visible : Visibility.Collapsed;
        if (PopupStyleGlassHint is not null)
        {
            PopupStyleGlassHint.Text = YitaPalette.IsActive(palette)
                ? (_uiLanguage == "zh-CN" ? "暖色透明外层搭配不透明奶油白阅读面，避免桌面背景干扰正文。"
                    : "Warm glass surrounds an opaque ivory reading surface to keep text clear.")
                : L(isColorGlass ? "PopupStyleColorGlassHint" : "PopupStyleGlassHint");
            PopupStyleGlassHint.Visibility = isGlass ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateProviderFields()
    {
        var isDeepSeek = (ProviderComboBox.SelectedValue as string) == "deepseek";
        EndpointTextBox.IsEnabled = isDeepSeek;
        ModelComboBox.IsEnabled = isDeepSeek;
        ApiKeyPasswordBox.IsEnabled = isDeepSeek;
        TestConnectionButton.IsEnabled = true;
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        var providerId = ProviderComboBox.SelectedValue as string ?? "deepseek";
        var endpointText = EndpointTextBox.Text.Trim();
        var model = ReadComboText(ModelComboBox);
        var apiKey = ApiKeyPasswordBox.Password.Trim();
        if (providerId == "deepseek"
            && (!TranslationProviderFactory.TryValidateEndpoint(endpointText, out _)
                || string.IsNullOrWhiteSpace(model)
                || string.IsNullOrWhiteSpace(apiKey)))
        {
            SetLocalizedConnectionStatus("FillConfiguration", "DangerBrush");
            return;
        }

        TestConnectionButton.IsEnabled = false;
        SetLocalizedConnectionStatus("Connecting", "AppMutedTextBrush");
        _connectionTestCancellation?.Cancel();
        _connectionTestCancellation?.Dispose();
        var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _connectionTestCancellation = timeout;
        try
        {
            using var factory = new TranslationProviderFactory();
            var provider = factory.Create(new AppSettings
            {
                ProviderId = providerId,
                DeepSeekEndpoint = endpointText,
                DeepSeekModel = model,
                DeepSeekApiKey = apiKey,
                TranslationMode = TranslationPreferenceCatalog.NormalizeMode(
                    ReadComboText(TranslationModeComboBox)),
                TranslationTone = TranslationPreferenceCatalog.NormalizeTone(
                    ReadComboText(TranslationToneComboBox)),
                PersonalGlossary = PersonalGlossaryTextBox.Text.Trim(),
            });
            var receivedText = false;
            await foreach (var chunk in provider.TranslateAsync(
                               new TranslationRequest(
                                   "hello",
                                   "英语",
                                   "简体中文",
                                   Mode: TranslationPreferenceCatalog.NormalizeMode(
                                       ReadComboText(TranslationModeComboBox)),
                                   Tone: TranslationPreferenceCatalog.NormalizeTone(
                                       ReadComboText(TranslationToneComboBox)),
                                   PersonalGlossary: PersonalGlossaryTextBox.Text.Trim()),
                               timeout.Token))
            {
                if (!string.IsNullOrWhiteSpace(chunk.TextDelta))
                {
                    receivedText = true;
                    break;
                }
            }

            if (_isClosed)
            {
                return;
            }

            if (!receivedText)
            {
                throw new TranslationProviderException(L("NoTestTranslation"));
            }

            SetLocalizedConnectionStatus("ConnectionSuccess", "SuccessBrush");
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed)
            {
                SetLocalizedConnectionStatus("ConnectionTimeout", "DangerBrush");
            }
        }
        catch (TranslationProviderException exception)
        {
            if (_isClosed)
            {
                return;
            }

            var message = UiLanguageCatalog.LocalizeProviderError(_uiLanguage, exception.Message);
            SetConnectionStatus(
                message.Length <= 58 ? message : message[..58] + "…",
                "DangerBrush");
        }
        catch (Exception)
        {
            if (_isClosed)
            {
                return;
            }

            SetConnectionStatus(
                _uiLanguage == UiLanguageCatalog.SimplifiedChineseLanguageId
                    ? "连接失败，请检查配置"
                    : "Connection failed. Check the configuration.",
                "DangerBrush");
        }
        finally
        {
            if (ReferenceEquals(_connectionTestCancellation, timeout))
            {
                _connectionTestCancellation = null;
            }

            timeout.Dispose();
            if (!_isClosed)
            {
                TestConnectionButton.IsEnabled = true;
            }
        }
    }

    private void ClearApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        _apiKeyClearRequested = true;
        ApiKeyPasswordBox.Clear();
        SetLocalizedConnectionStatus("DeleteAfterSave", "AppMutedTextBrush");
        ApiKeyPasswordBox.Focus();
    }

    private void SetConnectionStatus(string text, string brushResourceKey)
    {
        _connectionStatusLocalizationKey = null;
        ConnectionStatusText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            brushResourceKey);
        ConnectionStatusText.Text = text;
    }

    private void SetLocalizedConnectionStatus(string localizationKey, string brushResourceKey)
    {
        _connectionStatusLocalizationKey = localizationKey;
        ConnectionStatusText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            brushResourceKey);
        ConnectionStatusText.Text = L(localizationKey);
    }

    private void DefaultFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DefaultFontSizeValueText is not null)
        {
            DefaultFontSizeValueText.Text = e.NewValue.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
