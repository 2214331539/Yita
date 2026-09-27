using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Yita.Interop;
using Yita.Models;
using Yita.Selection;
using Yita.Settings;
using Yita.Translation;
using Forms = System.Windows.Forms;
using WpfClipboard = System.Windows.Clipboard;
using WpfButton = System.Windows.Controls.Button;
using WpfCanvas = System.Windows.Controls.Canvas;

namespace Yita.Windows;

internal partial class PopupWindow : Window
{
    private const double DefaultTranslationFontSize = 16.5;
    private const string DefaultEnglishTranslationFontFamily = TranslationFontCatalog.DefaultEnglishFontFamily;
    private const string DefaultChineseTranslationFontFamily = TranslationFontCatalog.DefaultChineseFontFamily;
    private const int AutomaticSizeGrowthThreshold = 12;
    private const double MinimumExplanationWindowHeight = 278;
    private const double BubbleHorizontalInset = 12;
    private const double BubbleVerticalInset = 3;
    private const double BubbleV2HorizontalInset = 14;
    private const double BubbleV2VerticalInset = 4;
    private const int StreamingRevealInitialCharacters = 12;
    private const int StreamingRevealTargetFrames = 4;
    private const int StreamingRevealMinimumCharactersPerFrame = 4;
    private const int StreamingRevealMaximumCharactersPerFrame = 96;
    private static readonly TimeSpan StreamingRevealInterval = TimeSpan.FromMilliseconds(16);
    private static readonly Geometry LegacyBubbleLeftTailGeometry = CreateFrozenGeometry("M0,0 L18,8 L0,16 Z");
    private static readonly Geometry LegacyBubbleRightTailGeometry = CreateFrozenGeometry("M18,0 L0,8 L18,16 Z");
    private static readonly Geometry BubbleV2LeftTailGeometry = CreateFrozenGeometry(
        "M0,8 C4,5 8,2 16,0 C13,5 13,11 16,16 C8,14 4,11 0,8 Z");
    private static readonly Geometry BubbleV2RightTailGeometry = CreateFrozenGeometry(
        "M16,8 C12,5 8,2 0,0 C3,5 3,11 0,16 C8,14 12,11 16,8 Z");
    private System.Windows.Media.FontFamily _chineseTranslationFont;
    private System.Windows.Media.FontFamily _englishTranslationFont;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly DispatcherTimer _translationRevealTimer;
    private readonly DispatcherTimer _explanationRevealTimer;
    private HwndSource? _hwndSource;
    private IntPtr _windowHandle;
    private string _sourceText = string.Empty;
    private string _displayedSourceText = string.Empty;
    private bool _showingSource;
    private string _currentTranslationSourceText = string.Empty;
    private string _currentSourceLanguage = "自动检测";
    private string _translatedText = string.Empty;
    private CompletedTranslation? _lastCompletedTranslation;
    private string _translationBeforeEdit = string.Empty;
    private string _targetLanguage = LanguageDirectionResolver.Chinese;
    private readonly PopupPlacementState _placementState = new();
    private ScreenPoint _anchorPoint => _placementState.Anchor;
    private bool _isUserMoving;
    private bool _isClosed;
    private bool _hasUserMoved => _placementState.UserMoved;
    internal PopupPlacement? PreferredPlacement
    {
        get => _placementState.Preference;
        set => _placementState.Preference = value;
    }
    internal event Action<PopupPlacement>? PlacementChanged;
    internal event Action<Exception>? RenderingFailed;
    private bool _hasDisplayedTranslation;
    private Paragraph? _translationParagraph;
    private string _renderedTranslation = string.Empty;
    private string _translationRevealTarget = string.Empty;
    private HighlightedText _translationRevealDocument = HighlightedText.Empty;
    private HighlightedText _renderedTranslationDocument = HighlightedText.Empty;
    private bool _completeTranslationAfterReveal;
    private bool _replaceTranslationOnNextUpdate;
    private int _copyFeedbackVersion;
    private bool _isRepositionPending;
    private string _uiLanguage = "en";
    private ActionMessageKind _actionMessageKind;
    private BodyMessageKind _bodyMessageKind = BodyMessageKind.Translating;
    private string? _customFailureMessage;
    private bool _isEditingTranslation;
    private bool _isAutomaticSizing;
    private int _lastAutomaticSizeTextLength;
    private string _explanationText = string.Empty;
    private Paragraph? _explanationParagraph;
    private string _renderedExplanation = string.Empty;
    private string _explanationRevealTarget = string.Empty;
    private HighlightedText _explanationRevealDocument = HighlightedText.Empty;
    private HighlightedText _renderedExplanationDocument = HighlightedText.Empty;
    private string _selectedExplanationText = string.Empty;
    private ExplanationInvocation? _lastExplanationInvocation;
    private bool _isExplanationVisible;
    private bool _isTranslationComplete;
    private bool _isExplanationComplete;
    private bool _isExplanationRecordSaving;
    private bool _isExplanationRecorded;
    private string? _explanationRecordErrorCode;
    private int _explanationRecordVersion;
    private int _selectionActionVersion;
    private bool _isBubbleVisualStyle;
    private bool _isBubbleV2VisualStyle;
    private bool _isBubbleV3VisualStyle;
    private bool UsesSculptedBubbleGeometry => _isBubbleV2VisualStyle || _isBubbleV3VisualStyle;

    public PopupWindow(
        long requestId,
        double defaultFontSize = DefaultTranslationFontSize,
        string englishFontFamily = DefaultEnglishTranslationFontFamily,
        string chineseFontFamily = DefaultChineseTranslationFontFamily,
        string uiLanguage = "en",
        string popupVisualStyle = PopupVisualStyleCatalog.DefaultStyleId)
    {
        RequestId = requestId;
        HistorySessionId = Guid.NewGuid();
        HistoryCreatedAt = DateTimeOffset.Now;
        _englishTranslationFont = CreateFontFamily(englishFontFamily, DefaultEnglishTranslationFontFamily);
        _chineseTranslationFont = CreateFontFamily(chineseFontFamily, DefaultChineseTranslationFontFamily);
        InitializeComponent();
        SizePresetBar.ConfigureFontSize(12, 34, defaultFontSize);
        SizePresetBar.PresetSelected += SizePresetBar_PresetSelected;
        SizePresetBar.FontSizeChanged += SizePresetBar_FontSizeChanged;
        _translationRevealTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = StreamingRevealInterval,
        };
        _translationRevealTimer.Tick += TranslationRevealTimer_Tick;
        _explanationRevealTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = StreamingRevealInterval,
        };
        _explanationRevealTimer.Tick += ExplanationRevealTimer_Tick;
        ApplyVisualStyle(popupVisualStyle);
        TranslationRichTextBox.FontFamily = _englishTranslationFont;
        ApplyTranslationFontSize(SizePresetBar.FontSizeValue);
        ApplyUiLanguage(uiLanguage);
        SourceViewButton.IsEnabled = false;
        SourceInitialized += OnSourceInitialized;
    }

    public long RequestId { get; }

    public Guid HistorySessionId { get; }

    public DateTimeOffset HistoryCreatedAt { get; }

    public bool IsPinned { get; private set; }

    public bool HasTranslation => _lastCompletedTranslation is not null;

    public string SourceText => _sourceText;

    public string CurrentTranslationSourceText => _currentTranslationSourceText;

    public string CurrentSourceLanguage => _currentSourceLanguage;

    public string LanguageSwitchSourceText => _translatedText;

    public string CurrentTargetLanguage => _targetLanguage;

    public string CurrentTranslationText => _translatedText;

    public string CurrentExplanationText => _explanationText;

    public string CurrentExplanationSubjectText => _lastExplanationInvocation?.SubjectText ?? string.Empty;

    public ExplanationScope CurrentExplanationScope =>
        _lastExplanationInvocation?.Scope ?? ExplanationScope.SourceText;

    public string UiLanguage => _uiLanguage;

    public ScreenPoint AnchorPoint => _anchorPoint;

    public bool ContainsScreenPoint(ScreenPoint point)
    {
        return IsVisible
               && _windowHandle != IntPtr.Zero
               && NativeMethods.GetWindowRect(_windowHandle, out var rectangle)
               && rectangle.Contains(point);
    }

    public event Action<PopupWindow, bool>? PinStateChanged;

    public event Action<PopupWindow, string>? RetranslateRequested;

    public event Func<PopupWindow, string, bool>? CorrectionSaveRequested;

    public event Action<PopupWindow, string, ExplanationScope>? ExplanationRequested;

    public event Action<PopupWindow>? ExplanationDismissed;

    public event Action<PopupWindow, string, QuestionContextKind>? QuestionSubmitted;

    public event Action<PopupWindow>? DeepSeekChatRequested;

    internal event Action<PopupWindow>? ManualExplanationRecordRequested;

    public void ApplyUiLanguage(string? uiLanguage)
    {
        _uiLanguage = string.Equals(uiLanguage, "zh-CN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uiLanguage, "zh", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : "en";

        DragHandle.ToolTip = Localize("Drag to move", "拖动移动窗口");
        SizePresetBar.ApplyUiLanguage(_uiLanguage);
        System.Windows.Automation.AutomationProperties.SetName(
            DragHandle,
            Localize("Drag window", "拖动窗口"));
        DirectionButton.ToolTip = Localize("Switch translation language", "切换翻译语言");
        ExplainButton.Content = Localize("Explain", "解释");
        ExplainButton.ToolTip = Localize("Explain source text", "解释原文");
        CodeAnalysisButton.Content = Localize("Code", "代码");
        CodeAnalysisButton.ToolTip = Localize("Analyze selected code", "分析所选代码");
        SelectionExplainButton.Content = Localize("Explain", "解释");
        SelectionExplainButton.ToolTip = Localize("Explain selected translation", "解释所选译文");
        CopySourceButton.Content = Localize("Source", "原文");
        CopyTranslationButton.Content = Localize("Translation", "译文");
        SourceViewButton.Content = Localize("Source", "原文");
        TranslationViewButton.Content = Localize("Translation", "译文");
        SourceViewButton.ToolTip = Localize("Show source", "查看原文");
        TranslationViewButton.ToolTip = Localize("Show translation", "查看译文");
        CopySourceSelectionMenuItem.Header = Localize("Copy", "复制");
        SelectAllSourceMenuItem.Header = Localize("Select all", "全选");
        System.Windows.Automation.AutomationProperties.SetName(SourceViewButton, Localize("Show source", "查看原文"));
        System.Windows.Automation.AutomationProperties.SetName(TranslationViewButton, Localize("Show translation", "查看译文"));
        CopySourceButton.ToolTip = Localize("Copy source", "复制原文");
        CopyTranslationButton.ToolTip = Localize("Copy translation", "复制译文");
        EditTranslationButton.ToolTip = _isEditingTranslation
            ? Localize("Save correction (Ctrl+Enter)", "保存修正（Ctrl+Enter）")
            : Localize("Edit and save correction (Ctrl+E)", "编辑并保存修正（Ctrl+E）");
        PinButton.ToolTip = IsPinned
            ? Localize("Release window", "取消保留")
            : Localize("Keep window", "保留此窗口");
        CloseButton.ToolTip = Localize("Close", "关闭");
        ExplanationTitleText.Text = GetExplanationTitle();
        ExplanationCopyButton.Content = Localize("Copy", "复制");
        ExplanationCopyButton.ToolTip = Localize("Copy explanation", "复制解释");
        UpdateExplanationRecordButton();
        ExplanationRetryButton.Content = Localize("Retry", "重试");
        ExplanationRetryButton.ToolTip = Localize("Retry explanation", "重新解释");
        ExplanationBackButton.Content = Localize("Back", "返回");
        ExplanationBackButton.ToolTip = Localize("Back to translation", "返回译文");
        InlineAskButton.Content = Localize("Ask", "提问");
        InlineQuestionPlaceholder.Text = Localize("Ask about this…", "对此提问…");
        InlineQuestionTextBox.ToolTip = Localize(
            "Ask directly; Enter sends, Shift+Enter adds a line",
            "直接提问；Enter 发送，Shift+Enter 换行");
        DeepSeekChatButton.ToolTip = Localize(
            "Open DeepSeek quick chat",
            "打开 DeepSeek 快速聊天");
        CopySelectionMenuItem.Header = Localize("Copy", "复制");
        SelectAllMenuItem.Header = Localize("Select all", "全选");
        CopyExplanationSelectionMenuItem.Header = Localize("Copy", "复制");
        SelectAllExplanationMenuItem.Header = Localize("Select all", "全选");
        System.Windows.Automation.AutomationProperties.SetName(
            DirectionButton,
            Localize("Switch translation language", "切换翻译语言"));
        System.Windows.Automation.AutomationProperties.SetName(
            ExplainButton,
            Localize("Explain source text", "解释原文"));
        System.Windows.Automation.AutomationProperties.SetName(
            CodeAnalysisButton,
            Localize("Analyze selected code", "分析所选代码"));
        System.Windows.Automation.AutomationProperties.SetName(
            SelectionExplainButton,
            Localize("Explain selected translation", "解释所选译文"));
        System.Windows.Automation.AutomationProperties.SetName(
            CopySourceButton,
            Localize("Copy source", "复制原文"));
        System.Windows.Automation.AutomationProperties.SetName(
            CopyTranslationButton,
            Localize("Copy translation", "复制译文"));
        System.Windows.Automation.AutomationProperties.SetName(
            EditTranslationButton,
            _isEditingTranslation
                ? Localize("Save correction", "保存修正")
                : Localize("Edit translation", "编辑译文"));
        System.Windows.Automation.AutomationProperties.SetName(
            PinButton,
            IsPinned ? Localize("Release window", "取消保留") : Localize("Keep window", "保留此窗口"));
        System.Windows.Automation.AutomationProperties.SetName(
            CloseButton,
            Localize("Close", "关闭"));
        System.Windows.Automation.AutomationProperties.SetName(
            ExplanationCopyButton,
            Localize("Copy explanation", "复制解释"));
        System.Windows.Automation.AutomationProperties.SetName(
            ExplanationRecordButton,
            Localize("Record explanation", "记录解释"));
        System.Windows.Automation.AutomationProperties.SetName(
            ExplanationRetryButton,
            Localize("Retry explanation", "重新解释"));
        System.Windows.Automation.AutomationProperties.SetName(
            ExplanationBackButton,
            Localize("Back to translation", "返回译文"));
        System.Windows.Automation.AutomationProperties.SetName(
            InlineQuestionTextBox,
            Localize("Ask AI about this content", "向 AI 提问当前内容"));
        System.Windows.Automation.AutomationProperties.SetName(
            InlineAskButton,
            Localize("Send question", "发送问题"));
        System.Windows.Automation.AutomationProperties.SetName(
            DeepSeekChatButton,
            Localize("Open DeepSeek quick chat", "打开 DeepSeek 快速聊天"));

        UpdateDirectionButtonText();
        UpdateBodyMessageText();
        UpdateActionMessageText();
    }

    public void ApplyAppearance(
        string? englishFontFamily,
        string? chineseFontFamily,
        string? uiLanguage,
        string? popupVisualStyle)
    {
        _englishTranslationFont = CreateFontFamily(
            englishFontFamily,
            DefaultEnglishTranslationFontFamily);
        _chineseTranslationFont = CreateFontFamily(
            chineseFontFamily,
            DefaultChineseTranslationFontFamily);
        ApplyVisualStyle(popupVisualStyle);
        ApplyUiLanguage(uiLanguage);
        UpdateSourceText(_displayedSourceText, force: true);

        if (_translatedText.Length > 0 && !_isEditingTranslation)
        {
            var completeAfterAppearanceUpdate = _completeTranslationAfterReveal;
            StopTranslationReveal(clearTarget: false);
            _translationRevealTarget = _translatedText;
            if (!string.Equals(_translationRevealDocument.PlainText, _translatedText, StringComparison.Ordinal))
            {
                _translationRevealDocument = HighlightMarkup.EnsureEmphasis(
                    HighlightedText.FromPlainText(_translatedText));
            }
            _translationParagraph = null;
            _renderedTranslation = string.Empty;
            _renderedTranslationDocument = HighlightedText.Empty;
            SetTranslationText(_translatedText);
            if (completeAfterAppearanceUpdate)
            {
                CompleteTranslationPresentation();
            }
        }

        if (_explanationText.Length > 0)
        {
            StopExplanationReveal(clearTarget: false);
            _explanationRevealTarget = _explanationText;
            if (!string.Equals(_explanationRevealDocument.PlainText, _explanationText, StringComparison.Ordinal))
            {
                _explanationRevealDocument = HighlightMarkup.EnsureEmphasis(
                    HighlightedText.FromPlainText(_explanationText));
            }
            _explanationParagraph = null;
            _renderedExplanation = string.Empty;
            _renderedExplanationDocument = HighlightedText.Empty;
            SetExplanationText(_explanationText);
        }

        if (_isAutomaticSizing && _translatedText.Length > 0)
        {
            ApplyAutomaticSize(_translatedText, _anchorPoint, force: true);
        }
        else if (IsVisible && !IsPinned)
        {
            SchedulePositionNearAnchor();
        }
    }

    private void ApplyVisualStyle(string? popupVisualStyle)
    {
        var normalizedStyle = PopupVisualStyleCatalog.Normalize(popupVisualStyle);
        _isBubbleVisualStyle = PopupVisualStyleCatalog.IsBubble(normalizedStyle)
            && !SystemParameters.HighContrast;
        _isBubbleV2VisualStyle = _isBubbleVisualStyle
            && PopupVisualStyleCatalog.IsBubbleV2(normalizedStyle);
        _isBubbleV3VisualStyle = _isBubbleVisualStyle
            && PopupVisualStyleCatalog.IsBubbleV3(normalizedStyle);

        PopupSurface.Margin = _isBubbleVisualStyle
            ? new Thickness(
                CurrentBubbleHorizontalInset,
                CurrentBubbleVerticalInset,
                CurrentBubbleHorizontalInset,
                CurrentBubbleVerticalInset)
            : new Thickness(0);
        PopupSurface.Padding = new Thickness(22, 6, 22, 20);
        BubbleHighlight.Visibility = _isBubbleVisualStyle ? Visibility.Visible : Visibility.Collapsed;
        ConfigureBubbleTailGeometry();
        UpdateBubbleTailDirection(windowIsLeftOfAnchor: false);

        if (SizeToContent == SizeToContent.Manual)
        {
            ConfigureResizableLayout(_anchorPoint);
        }

        UpdatePopupSurfaceClip();
        UpdateExplanationSurfacePresentation();
    }

    private void UpdateExplanationSurfacePresentation()
    {
        // Keep the original document and its selection/layout intact, but do not
        // draw it through a transparent explanation or leave it mouse-interactive.
        TranslationRichTextBox.Opacity = _isExplanationVisible ? 0 : 1;
        TranslationRichTextBox.IsHitTestVisible = !_isExplanationVisible;
        // Also hide the translation's reading card without collapsing its layout.
        // Returning restores the same document, selection and viewport size.
        TranslationTextSurface.Opacity = _isExplanationVisible ? 0 : 1;
        TranslationTextSurface.IsHitTestVisible = !_isExplanationVisible;
        PopupSurface.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,
            _isBubbleV3VisualStyle && _isExplanationVisible ? "ExplanationSurfaceBrush" : "PopupBackgroundBrush");
        if (_isBubbleV3VisualStyle)
        {
            // Use a single glass surface, not two stacked translucent panes.
            ExplanationOverlay.Background = System.Windows.Media.Brushes.Transparent;
        }
        else
        {
            ExplanationOverlay.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty,
                "ExplanationSurfaceBrush");
        }
    }

    private void UpdateBubbleTailDirection(bool windowIsLeftOfAnchor)
    {
        BubbleLeftTail.Visibility = _isBubbleVisualStyle && !windowIsLeftOfAnchor
            ? Visibility.Visible
            : Visibility.Collapsed;
        BubbleRightTail.Visibility = _isBubbleVisualStyle && windowIsLeftOfAnchor
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private double CurrentBubbleHorizontalInset => UsesSculptedBubbleGeometry
        ? BubbleV2HorizontalInset
        : BubbleHorizontalInset;

    private double CurrentBubbleVerticalInset => UsesSculptedBubbleGeometry
        ? BubbleV2VerticalInset
        : BubbleVerticalInset;

    private double PopupSurfaceHorizontalInset => _isBubbleVisualStyle ? CurrentBubbleHorizontalInset * 2 : 0;

    private double PopupSurfaceVerticalInset => _isBubbleVisualStyle ? CurrentBubbleVerticalInset * 2 : 0;

    private void ConfigureBubbleTailGeometry()
    {
        BubbleLeftTail.Data = UsesSculptedBubbleGeometry
            ? BubbleV2LeftTailGeometry
            : LegacyBubbleLeftTailGeometry;
        BubbleRightTail.Data = UsesSculptedBubbleGeometry
            ? BubbleV2RightTailGeometry
            : LegacyBubbleRightTailGeometry;
        WpfCanvas.SetLeft(BubbleLeftTail, UsesSculptedBubbleGeometry ? 0 : 2);
        WpfCanvas.SetRight(BubbleRightTail, UsesSculptedBubbleGeometry ? 0 : 2);
        WpfCanvas.SetTop(BubbleLeftTail, UsesSculptedBubbleGeometry ? 30 : 31);
        WpfCanvas.SetTop(BubbleRightTail, UsesSculptedBubbleGeometry ? 30 : 31);
    }

    internal void SelectTextForVisualTest(int start, int length)
    {
        if (_translationParagraph is null || _renderedTranslation.Length == 0)
        {
            return;
        }

        var safeStart = Math.Clamp(start, 0, _renderedTranslation.Length);
        var safeLength = Math.Clamp(length, 0, _renderedTranslation.Length - safeStart);
        var selectionStart = GetTranslationPointerAtOffset(safeStart);
        var selectionEnd = GetTranslationPointerAtOffset(safeStart + safeLength);
        if (selectionStart is null || selectionEnd is null)
        {
            return;
        }

        Activate();
        TranslationRichTextBox.Focus();
        TranslationRichTextBox.Selection.Select(selectionStart, selectionEnd);
        ScheduleSelectionExplainButton();
    }

    internal void ConfigureViewportForVisualTest(double width, double height, double fontSize)
    {
        EnterManualSizeMode(updateLayout: true);
        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
        SizePresetBar.FontSizeValue = fontSize;
        UpdateLayout();
    }

    internal bool HasActiveSelectionForVisualTest()
    {
        return IsActive
            && TranslationRichTextBox.IsKeyboardFocusWithin
            && !TranslationRichTextBox.Selection.IsEmpty
               && !string.IsNullOrWhiteSpace(TranslationRichTextBox.Selection.Text);
    }

    internal bool HasSelectionExplainButtonForVisualTest() =>
        SelectionExplainButton.Visibility == Visibility.Visible;

    internal bool IsCodeAnalysisEnabledForVisualTest() => CodeAnalysisButton.IsEnabled;

    internal void StartCodeAnalysisForVisualTest()
    {
        if (!HasTranslation)
        {
            return;
        }

        _lastExplanationInvocation = new ExplanationInvocation(
            _currentTranslationSourceText,
            ExplanationScope.CodeAnalysis);
        ShowExplanationLoading();
    }

    internal bool IsCodeAnalysisPresentationForVisualTest() =>
        IsCodeAnalysisActive
        && string.Equals(ExplanationTitleText.Text, "Code Analysis", StringComparison.Ordinal);

    internal bool IsExplanationVisibleForVisualTest() =>
        _isExplanationVisible && ExplanationOverlay.Visibility == Visibility.Visible;

    internal bool HasHiddenTranslationForExplanationForVisualTest() =>
        _isExplanationVisible
        && TranslationRichTextBox.Opacity == 0
        && !TranslationRichTextBox.IsHitTestVisible
        && TranslationTextSurface.Opacity == 0
        && !TranslationTextSurface.IsHitTestVisible;

    internal bool HasRestoredTranslationPresentationForVisualTest() =>
        !_isExplanationVisible
        && TranslationRichTextBox.Opacity == 1
        && TranslationRichTextBox.IsHitTestVisible
        && TranslationTextSurface.Opacity == 1
        && TranslationTextSurface.IsHitTestVisible;

    internal bool HasReadableGlassTextSurfaceForVisualTest()
    {
        UpdateLayout();
        var surface = _isExplanationVisible ? ExplanationTextSurface : TranslationTextSurface;
        var textBox = _isExplanationVisible ? ExplanationRichTextBox : TranslationRichTextBox;
        return surface.Background is LinearGradientBrush brush
               && brush.GradientStops.All(stop => stop.Color.A == 255
                   && stop.Color != System.Windows.Media.Colors.White)
               && brush.Opacity == 1
               && surface.Opacity == 1
               && surface.CornerRadius == new CornerRadius(18)
               && surface.Padding == new Thickness(12)
               && surface.ActualWidth >= textBox.ActualWidth + 23
               && surface.ActualHeight >= textBox.ActualHeight + 23
               && textBox.IsHitTestVisible
               && textBox.Effect is null;
    }

    internal bool IsBubbleVisualStyleForVisualTest() => _isBubbleVisualStyle;

    internal bool IsBubbleV2VisualStyleForVisualTest() => _isBubbleV2VisualStyle;

    internal bool HasUsableGlassRimForVisualTest()
    {
        UpdateLayout();
        return _isBubbleV3VisualStyle
               && GlassSurfaceRim.IsGlassEnabled
               && !GlassSurfaceRim.IsHitTestVisible
               && Math.Abs(GlassSurfaceRim.ActualWidth - PopupSurface.ActualWidth) < 1
               && Math.Abs(GlassSurfaceRim.ActualHeight - PopupSurface.ActualHeight) < 1
               && Opacity == 1;
    }

    internal bool HasUsableDragHandleForVisualTest()
    {
        UpdateLayout();
        return DragHandle.IsHitTestVisible
               && DragHandle.ActualWidth >= 160
               && DragHandle.ActualHeight >= 28;
    }

    internal bool VerifyAppearancePreservesEditForVisualTest(string style)
    {
        var original = _translatedText;
        BeginTranslationEditing();
        if (!_isEditingTranslation) return false;
        try
        {
            TranslationRichTextBox.SelectAll();
            TranslationRichTextBox.Selection.Text = "Unsaved correction / 未保存的修正";
            TranslationRichTextBox.SelectAll();
            var document = TranslationRichTextBox.Document;
            var selection = TranslationRichTextBox.Selection.Text;
            var canUndo = TranslationRichTextBox.CanUndo;
            ApplyAppearance(_englishTranslationFont.Source, _chineseTranslationFont.Source, _uiLanguage, style);
            if (!ReferenceEquals(document, TranslationRichTextBox.Document)
                || TranslationRichTextBox.Selection.Text != selection
                || !canUndo || !TranslationRichTextBox.CanUndo
                || !_isEditingTranslation || TranslationRichTextBox.IsReadOnly)
                return false;
        }
        finally { CancelTranslationEditing(showStatus: false); }
        return _translatedText == original && TranslationRichTextBox.IsReadOnly;
    }

    internal void ApplySizePresetForVisualTest(WindowSizePreset preset) => ApplySizePreset(preset);

    internal bool HasMinimalReadingToolbarForVisualTest() =>
        SizePresetBar.Visibility == Visibility.Collapsed
        && DirectionButton.Visibility == Visibility.Collapsed
        && ExplainButton.Visibility == Visibility.Collapsed
        && CodeAnalysisButton.Visibility == Visibility.Collapsed
        && EditTranslationButton.Visibility == Visibility.Collapsed
        && SourceViewButton.IsVisible && TranslationViewButton.IsVisible
        && PinButton.IsVisible && CloseButton.IsVisible;

    internal bool HasSingleRowExternalControlsForVisualTest() =>
        HeaderBarGrid.DesiredSize.Width <= ActualWidth + 0.5;

    internal void SetFontSizeForVisualTest(double value) => SizePresetBar.FontSizeValue = value;

    internal double TranslationFontSizeForVisualTest => TranslationRichTextBox.FontSize;

    internal bool HasContentHighlightsForVisualTest() =>
        HighlightedTextRenderer.HasHighlightRuns(_translationParagraph)
        || HighlightedTextRenderer.HasHighlightRuns(_explanationParagraph);

    internal bool HasVerticalOverflowForVisualTest()
    {
        UpdateLayout();
        return TranslationRichTextBox.ExtentHeight > TranslationRichTextBox.ViewportHeight + 1;
    }

    public void ShowLoading(ScreenPoint anchorPoint)
    {
        HideExplanation(notifyDismissed: true);
        HideSelectionExplainButton();
        CancelTranslationEditing(showStatus: false);
        var isRetranslating = _hasDisplayedTranslation;
        StopTranslationReveal(clearTarget: true);
        _replaceTranslationOnNextUpdate = isRetranslating;
        if (_hasDisplayedTranslation)
        {
            PreserveCurrentWindowSize();
            LoadingPanel.Visibility = Visibility.Collapsed;
            TranslationRichTextBox.Visibility = Visibility.Visible;
            SetActionBarVisibility(Visibility.Visible);
            ShowActionStatus(ActionMessageKind.Retranslating);
        }
        else
        {
            TranslationRichTextBox.Visibility = Visibility.Collapsed;
            _bodyMessageKind = BodyMessageKind.Translating;
            _customFailureMessage = null;
            UpdateBodyMessageText();
            LoadingPanel.Visibility = Visibility.Collapsed;
            SetActionBarVisibility(Visibility.Collapsed);
        }

        _placementState.SetAnchor(anchorPoint, IsPinned && IsVisible);
        DirectionButton.IsEnabled = false;
        CopySourceButton.IsEnabled = false;
        CopyTranslationButton.IsEnabled = false;
        EditTranslationButton.IsEnabled = false;
        ExplainButton.IsEnabled = false;
        CodeAnalysisButton.IsEnabled = false;
        _isTranslationComplete = false;
        UpdateQuestionBarState();
        if (_hasDisplayedTranslation)
        {
            ShowAt(anchorPoint);
        }
    }

    public void ShowTranslation(
        string sourceText,
        string translatedText,
        string targetLanguage,
        ScreenPoint anchorPoint,
        string sourceLanguage = "自动检测")
    {
        HideSelectionExplainButton();
        var highlightedTranslation = HighlightMarkup.EnsureEmphasis(HighlightMarkup.Parse(translatedText));
        var plainTranslation = highlightedTranslation.PlainText;
        var startsNewTranslation = !_hasDisplayedTranslation
            || _replaceTranslationOnNextUpdate
            || !string.Equals(_currentTranslationSourceText, sourceText, StringComparison.Ordinal)
            || !string.Equals(_targetLanguage, targetLanguage, StringComparison.Ordinal)
            || (_translationRevealTarget.Length > 0
                && !plainTranslation.StartsWith(_translationRevealTarget, StringComparison.Ordinal));
        if (string.IsNullOrEmpty(_sourceText))
        {
            _sourceText = sourceText;
        }
        _currentTranslationSourceText = sourceText;
        UpdateSourceText(_sourceText);
        _currentSourceLanguage = sourceLanguage;
        _translatedText = plainTranslation;
        _targetLanguage = targetLanguage;
        var anchorChanged = _placementState.SetAnchor(anchorPoint, IsPinned && IsVisible);
        _replaceTranslationOnNextUpdate = false;

        var isFirstTranslationUpdate = !_hasDisplayedTranslation;
        if (isFirstTranslationUpdate)
        {
            _isAutomaticSizing = true;
            _lastAutomaticSizeTextLength = 0;
        }

        QueueTranslationReveal(highlightedTranslation, resetVisibleText: startsNewTranslation);
        UpdateDirectionButtonText();
        LoadingPanel.Visibility = Visibility.Collapsed;
        SetActionBarVisibility(Visibility.Visible);
        DirectionButton.IsEnabled = false;
        CopySourceButton.IsEnabled = true;
        CopyTranslationButton.IsEnabled = true;
        EditTranslationButton.IsEnabled = false;
        ExplainButton.IsEnabled = false;
        CodeAnalysisButton.IsEnabled = false;
        _isTranslationComplete = false;
        UpdateQuestionBarState();
        TranslationRichTextBox.Visibility = Visibility.Visible;
        HideActionStatus();
        _hasDisplayedTranslation = true;
        ApplyAutomaticSize(_showingSource ? _displayedSourceText : plainTranslation, anchorPoint, force: isFirstTranslationUpdate);
        if (isFirstTranslationUpdate || !IsVisible || anchorChanged)
        {
            ShowAt(anchorPoint);
        }
    }

    public void MarkTranslationComplete()
    {
        if (_renderedTranslation.Length < _translationRevealTarget.Length)
        {
            _completeTranslationAfterReveal = true;
            EnsureTranslationRevealTimer();
            return;
        }

        CompleteTranslationPresentation();
    }

    private void CompleteTranslationPresentation()
    {
        _completeTranslationAfterReveal = false;
        _translationRevealTimer.Stop();
        ApplyAutomaticSize(_showingSource ? _displayedSourceText : _translatedText, _anchorPoint, force: true);
        if (!string.IsNullOrWhiteSpace(_translatedText))
        {
            _lastCompletedTranslation = new CompletedTranslation(
                _currentTranslationSourceText,
                _currentSourceLanguage,
                _translatedText,
                _targetLanguage);
        }

        if (HasTranslation && !_isEditingTranslation)
        {
            DirectionButton.IsEnabled = true;
            CopySourceButton.IsEnabled = true;
            CopyTranslationButton.IsEnabled = true;
            EditTranslationButton.IsEnabled = true;
            ExplainButton.IsEnabled = true;
            CodeAnalysisButton.IsEnabled = true;
        }

        _isTranslationComplete = HasTranslation;
        UpdateQuestionBarState();
    }

    public void RestoreTranslationAfterFailure()
    {
        if (_lastCompletedTranslation is not { } completed)
        {
            Close();
            return;
        }

        _currentTranslationSourceText = completed.SourceText;
        _currentSourceLanguage = completed.SourceLanguage;
        _translatedText = completed.TranslatedText;
        _targetLanguage = completed.TargetLanguage;
        StopTranslationReveal(clearTarget: true);
        _translationRevealTarget = _translatedText;
        _translationRevealDocument = HighlightMarkup.EnsureEmphasis(
            HighlightedText.FromPlainText(_translatedText));
        _translationParagraph = null;
        _renderedTranslation = string.Empty;
        _renderedTranslationDocument = HighlightedText.Empty;
        SetTranslationText(_translatedText);
        UpdateDirectionButtonText();

        LoadingPanel.Visibility = Visibility.Collapsed;
        SetActionBarVisibility(Visibility.Visible);
        DirectionButton.IsEnabled = true;
        CopySourceButton.IsEnabled = true;
        CopyTranslationButton.IsEnabled = true;
        EditTranslationButton.IsEnabled = true;
        ExplainButton.IsEnabled = true;
        CodeAnalysisButton.IsEnabled = true;
        _isTranslationComplete = true;
        UpdateQuestionBarState();
        TranslationRichTextBox.Visibility = Visibility.Visible;
        ShowActionStatus(ActionMessageKind.TranslationFailedPreserved);
    }

    public void ShowFailure(string? message = null)
    {
        SelectReadingView(showSource: false, animate: false);
        StopTranslationReveal(clearTarget: true);
        _replaceTranslationOnNextUpdate = false;
        _customFailureMessage = string.IsNullOrWhiteSpace(message) ? null : message;
        _bodyMessageKind = _customFailureMessage is null
            ? BodyMessageKind.Failure
            : BodyMessageKind.CustomFailure;
        UpdateBodyMessageText();
        TranslationRichTextBox.Visibility = Visibility.Collapsed;
        SetActionBarVisibility(Visibility.Visible);
        DirectionButton.IsEnabled = false;
        CopySourceButton.IsEnabled = false;
        CopyTranslationButton.IsEnabled = false;
        EditTranslationButton.IsEnabled = false;
        ExplainButton.IsEnabled = false;
        CodeAnalysisButton.IsEnabled = false;
        _isTranslationComplete = false;
        UpdateQuestionBarState();
        LoadingPanel.Visibility = Visibility.Visible;
        ShowAt(_anchorPoint);
    }

    public void ShowExplanationLoading()
    {
        if (!HasTranslation || _isEditingTranslation)
        {
            return;
        }

        PreserveCurrentWindowSize();
        Height = Math.Min(MaxHeight, Math.Max(ActualHeight, MinimumExplanationWindowHeight));
        if (IsVisible && !IsPinned)
        {
            SchedulePositionNearAnchor();
        }
        _isExplanationVisible = true;
        UpdateExplanationSurfacePresentation();
        _isExplanationComplete = false;
        _explanationRecordVersion++;
        _isExplanationRecordSaving = false;
        _isExplanationRecorded = false;
        _explanationRecordErrorCode = null;
        _explanationText = string.Empty;
        StopExplanationReveal(clearTarget: true);
        _explanationRevealDocument = HighlightedText.Empty;
        _explanationParagraph = null;
        _renderedExplanation = string.Empty;
        _renderedExplanationDocument = HighlightedText.Empty;
        HideSelectionExplainButton();
        ExplanationOverlay.Visibility = Visibility.Visible;
        ExplanationTitleText.Text = GetExplanationTitle();
        ExplanationStatusText.Text = GetExplanationLoadingText();
        ExplanationStatusText.Visibility = Visibility.Visible;
        ExplanationRichTextBox.Document.Blocks.Clear();
        ExplanationRichTextBox.Visibility = Visibility.Collapsed;
        ExplanationCopyButton.IsEnabled = false;
        UpdateExplanationRecordButton();
        ExplanationRetryButton.Visibility = Visibility.Collapsed;
        ExplainButton.Tag = IsCodeAnalysisActive ? null : "Selected";
        CodeAnalysisButton.Tag = IsCodeAnalysisActive ? "Selected" : null;
        ExplainButton.IsEnabled = false;
        CodeAnalysisButton.IsEnabled = false;
        UpdateQuestionBarState();
    }

    public void ShowExplanation(string explanation)
    {
        if (!_isExplanationVisible)
        {
            return;
        }

        var highlightedExplanation = HighlightMarkup.EnsureEmphasis(HighlightMarkup.Parse(explanation));
        var plainExplanation = highlightedExplanation.PlainText;
        var startsNewExplanation = _explanationRevealTarget.Length == 0
            || !plainExplanation.StartsWith(_explanationRevealTarget, StringComparison.Ordinal);
        _explanationText = plainExplanation;
        QueueExplanationReveal(highlightedExplanation, resetVisibleText: startsNewExplanation);
        ExplanationStatusText.Visibility = Visibility.Collapsed;
        ExplanationRichTextBox.Visibility = Visibility.Visible;
        ExplanationCopyButton.IsEnabled = !string.IsNullOrWhiteSpace(plainExplanation);
        ExplanationRetryButton.Visibility = Visibility.Collapsed;
    }

    public void MarkExplanationComplete()
    {
        if (!_isExplanationVisible || string.IsNullOrWhiteSpace(_explanationText))
        {
            return;
        }

        _isExplanationComplete = true;
        UpdateExplanationRecordButton();
        UpdateQuestionBarState();
    }

    public void ShowExplanationFailure(string message)
    {
        if (!_isExplanationVisible)
        {
            return;
        }

        _explanationRecordVersion++;
        _explanationText = string.Empty;
        StopExplanationReveal(clearTarget: true);
        _explanationRevealDocument = HighlightedText.Empty;
        _explanationParagraph = null;
        _renderedExplanation = string.Empty;
        _renderedExplanationDocument = HighlightedText.Empty;
        ExplanationRichTextBox.Document.Blocks.Clear();
        ExplanationRichTextBox.Visibility = Visibility.Collapsed;
        ExplanationStatusText.Text = message;
        ExplanationStatusText.Visibility = Visibility.Visible;
        ExplanationCopyButton.IsEnabled = false;
        _isExplanationRecordSaving = false;
        _isExplanationRecorded = false;
        _explanationRecordErrorCode = null;
        UpdateExplanationRecordButton();
        ExplanationRetryButton.Visibility = _lastExplanationInvocation is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        _isExplanationComplete = false;
        UpdateQuestionBarState();
    }

    public void HideExplanation(bool notifyDismissed)
    {
        var wasVisible = _isExplanationVisible || ExplanationOverlay.Visibility == Visibility.Visible;
        _isExplanationVisible = false;
        UpdateExplanationSurfacePresentation();
        _isExplanationComplete = false;
        _explanationRecordVersion++;
        _explanationText = string.Empty;
        StopExplanationReveal(clearTarget: true);
        _explanationParagraph = null;
        _renderedExplanation = string.Empty;
        ExplanationOverlay.Visibility = Visibility.Collapsed;
        ExplanationStatusText.Visibility = Visibility.Collapsed;
        ExplanationRichTextBox.Document.Blocks.Clear();
        ExplanationRichTextBox.Visibility = Visibility.Collapsed;
        ExplanationCopyButton.IsEnabled = false;
        _isExplanationRecordSaving = false;
        _isExplanationRecorded = false;
        _explanationRecordErrorCode = null;
        UpdateExplanationRecordButton();
        ExplanationRetryButton.Visibility = Visibility.Collapsed;
        ExplainButton.Tag = null;
        CodeAnalysisButton.Tag = null;
        ExplainButton.IsEnabled = HasTranslation && !_isEditingTranslation;
        CodeAnalysisButton.IsEnabled = HasTranslation && !_isEditingTranslation;
        UpdateQuestionBarState();

        if (wasVisible && notifyDismissed)
        {
            _lastExplanationInvocation = null;
            ExplanationDismissed?.Invoke(this);
        }

        if (wasVisible)
        {
            ScheduleSelectionExplainButton();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _selectionActionVersion++;
        SourceInitialized -= OnSourceInitialized;
        StopTranslationReveal(clearTarget: true);
        StopExplanationReveal(clearTarget: true);
        _translationRevealTimer.Tick -= TranslationRevealTimer_Tick;
        _explanationRevealTimer.Tick -= ExplanationRevealTimer_Tick;
        _lifetimeCancellation.Cancel();
        BeginAnimation(OpacityProperty, null);
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WindowProcedure);
            _hwndSource = null;
        }

        SizePresetBar.PresetSelected -= SizePresetBar_PresetSelected;
        SizePresetBar.FontSizeChanged -= SizePresetBar_FontSizeChanged;

        TranslationRichTextBox.Document.Blocks.Clear();
        ExplanationRichTextBox.Document.Blocks.Clear();
        _translationParagraph = null;
        _renderedTranslation = string.Empty;
        _translationRevealDocument = HighlightedText.Empty;
        _renderedTranslationDocument = HighlightedText.Empty;
        _explanationParagraph = null;
        _renderedExplanation = string.Empty;
        _explanationRevealDocument = HighlightedText.Empty;
        _renderedExplanationDocument = HighlightedText.Empty;
        _sourceText = string.Empty;
        _currentTranslationSourceText = string.Empty;
        _currentSourceLanguage = "自动检测";
        _translatedText = string.Empty;
        _explanationText = string.Empty;
        _selectedExplanationText = string.Empty;
        _lastExplanationInvocation = null;
        _isExplanationVisible = false;
        _lastCompletedTranslation = null;
        _windowHandle = IntPtr.Zero;
        base.OnClosed(e);
        _lifetimeCancellation.Dispose();
    }

    private void ShowAt(ScreenPoint anchorPoint)
    {
        var wasVisible = IsVisible;
        if (!IsVisible)
        {
            Opacity = 1;
            Show();
            UiMotion.Reveal(PopupRoot, scale: true);
        }

        UpdateLayout();
        UpdatePopupSurfaceClip();
        if (!IsPinned || !wasVisible)
        {
            PositionNear(anchorPoint);
        }

        if (!wasVisible && !IsPinned)
        {
            Dispatcher.BeginInvoke(
                () =>
                {
                    if (!_isClosed && IsVisible && !IsPinned)
                    {
                        PositionNear(_anchorPoint);
                    }
                },
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_windowHandle);
        _hwndSource?.AddHook(WindowProcedure);

        var currentStyles = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GwlExStyle).ToInt64();
        var requiredStyles = (currentStyles | NativeMethods.WsExToolWindow) & ~NativeMethods.WsExNoActivate;
        NativeMethods.SetWindowLongPtr(
            _windowHandle,
            NativeMethods.GwlExStyle,
            new IntPtr(requiredStyles));
    }

    private IntPtr WindowProcedure(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmSetCursor && IsVisible)
        {
            var hitTest = (int)unchecked((short)(lParam.ToInt64() & 0xffff));
            // A child control can report HTCLIENT for a few pixels next to the
            // edge. Re-resolve from the physical cursor position so the resize
            // cursor remains visible over the RichTextBox and other children.
            if (!IsResizeHitTest(hitTest))
            {
                hitTest = ResolveResizeHitTestAtCursor(windowHandle);
            }

            if (IsResizeHitTest(hitTest))
            {
                var cursor = NativeMethods.LoadCursor(
                    IntPtr.Zero,
                    GetResizeCursorId(hitTest));
                if (cursor != IntPtr.Zero)
                {
                    NativeMethods.SetCursor(cursor);
                    handled = true;
                    return new IntPtr(1);
                }
            }
        }

        if (message == NativeMethods.WmNcHitTest
            && IsVisible
            && NativeMethods.GetWindowRect(windowHandle, out var bounds))
        {
            var hitTest = ResolveResizeHitTest(
                bounds,
                PopupResizeHitTest.DecodeScreenPoint(lParam));
            if (hitTest != 0)
            {
                EnterManualSizeMode(updateLayout: false);
                handled = true;
                return new IntPtr(hitTest);
            }
        }

        return IntPtr.Zero;
    }

    private int ResolveResizeHitTestAtCursor(IntPtr windowHandle)
    {
        if (!NativeMethods.GetWindowRect(windowHandle, out var bounds)
            || !NativeMethods.GetCursorPos(out var cursor))
        {
            return 0;
        }

        return ResolveResizeHitTest(bounds, cursor.ToScreenPoint());
    }

    private int ResolveResizeHitTest(
        NativeMethods.NativeRect bounds,
        ScreenPoint point)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        // A generous physical hit target keeps resizing usable at 125–200%
        // DPI and on rounded surfaces whose visible edge is easy to miss.
        var edgeThickness = Math.Max(
            16,
            (int)Math.Ceiling(16 * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)));
        return PopupResizeHitTest.ResolveWithInsetTop(
            bounds,
            GetPopupSurfaceTop(bounds),
            point,
            edgeThickness);
    }

    private static bool IsResizeHitTest(int hitTest)
    {
        return hitTest is NativeMethods.HtLeft
            or NativeMethods.HtRight
            or NativeMethods.HtTop
            or NativeMethods.HtBottom
            or NativeMethods.HtTopLeft
            or NativeMethods.HtTopRight
            or NativeMethods.HtBottomLeft
            or NativeMethods.HtBottomRight;
    }

    private static IntPtr GetResizeCursorId(int hitTest)
    {
        return hitTest switch
        {
            NativeMethods.HtLeft or NativeMethods.HtRight => NativeMethods.CursorSizeWe,
            NativeMethods.HtTop or NativeMethods.HtBottom => NativeMethods.CursorSizeNs,
            NativeMethods.HtTopLeft or NativeMethods.HtBottomRight => NativeMethods.CursorSizeNwse,
            NativeMethods.HtTopRight or NativeMethods.HtBottomLeft => NativeMethods.CursorSizeNesw,
            _ => IntPtr.Zero,
        };
    }

    private int GetPopupSurfaceTop(NativeMethods.NativeRect windowBounds)
    {
        if (PopupSurface.ActualHeight <= 0)
        {
            return windowBounds.Top;
        }

        try
        {
            var surfaceTopLeft = PopupSurface.PointToScreen(new System.Windows.Point(0, 0));
            return Math.Clamp(
                (int)Math.Round(surfaceTopLeft.Y),
                windowBounds.Top,
                windowBounds.Bottom - 1);
        }
        catch (InvalidOperationException)
        {
            return windowBounds.Top;
        }
    }

    private async void CopySourceButton_Click(object sender, RoutedEventArgs e)
    {
        await CopyWithFeedbackAsync(CopySourceButton, _sourceText);
    }

    private void SourceViewButton_Click(object sender, RoutedEventArgs e) => SelectReadingView(showSource: true);

    private void TranslationViewButton_Click(object sender, RoutedEventArgs e) => SelectReadingView(showSource: false);

    internal void SelectReadingView(bool showSource, bool animate = true)
    {
        if (_isEditingTranslation || (showSource && _displayedSourceText.Length == 0)) return;
        if (_showingSource == showSource) return;
        HideExplanation(notifyDismissed: true);
        HideSelectionExplainButton();
        _showingSource = showSource;
        TranslationView.Visibility = showSource ? Visibility.Collapsed : Visibility.Visible;
        SourceRichTextBox.Visibility = showSource ? Visibility.Visible : Visibility.Collapsed;
        SourceViewButton.Tag = showSource ? "Selected" : null;
        TranslationViewButton.Tag = showSource ? null : "Selected";
        System.Windows.Automation.AutomationProperties.SetItemStatus(SourceViewButton,
            showSource ? Localize("Selected", "已选中") : string.Empty);
        System.Windows.Automation.AutomationProperties.SetItemStatus(TranslationViewButton,
            showSource ? string.Empty : Localize("Selected", "已选中"));
        if (animate) UiMotion.Reveal(showSource ? SourceRichTextBox : TranslationView);
        if (IsVisible && _isAutomaticSizing)
            ApplyAutomaticSize(showSource ? _displayedSourceText : _translatedText, _anchorPoint, force: true);
    }

    internal void UpdateSourceText(string text, bool force = false)
    {
        if (!force && _displayedSourceText == text) return;
        _displayedSourceText = text;
        var fontSize = SizePresetBar.FontSizeValue;
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = CalculateLineHeight(fontSize) };
        HighlightedTextRenderer.AppendTranslationRuns(paragraph, HighlightedText.FromPlainText(text),
            _englishTranslationFont, _chineseTranslationFont);
        SourceRichTextBox.Document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0), ColumnWidth = double.PositiveInfinity,
            FontFamily = _englishTranslationFont, FontSize = fontSize,
            Foreground = SourceRichTextBox.Foreground,
        };
        SourceViewButton.IsEnabled = text.Length > 0;
    }

    private async void CopyTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedText = TranslationRichTextBox.Selection.Text;
        var textToCopy = string.IsNullOrEmpty(selectedText) ? _translatedText : selectedText;
        await CopyWithFeedbackAsync(CopyTranslationButton, textToCopy);
    }

    private void EditTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isEditingTranslation)
        {
            TrySaveTranslationCorrection();
            return;
        }

        BeginTranslationEditing();
    }

    private void DirectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceText.Length == 0)
        {
            return;
        }

        HideExplanation(notifyDismissed: true);
        var nextTarget = LanguageDirectionResolver.GetOppositeTarget(_targetLanguage);
        RetranslateRequested?.Invoke(this, nextTarget);
    }

    private void ExplainButton_Click(object sender, RoutedEventArgs e)
    {
        RequestExplanation(
            ExplanationScope.SourceText,
            _currentTranslationSourceText);
    }

    private void CodeAnalysisButton_Click(object sender, RoutedEventArgs e)
    {
        RequestExplanation(
            ExplanationScope.CodeAnalysis,
            _currentTranslationSourceText);
    }

    private void SelectionExplainButton_Click(object sender, RoutedEventArgs e)
    {
        RequestExplanation(
            ExplanationScope.TranslationSelection,
            _selectedExplanationText);
    }

    private async void ExplanationCopyButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedText = TextNormalizer.Normalize(ExplanationRichTextBox.Selection.Text);
        await CopyWithFeedbackAsync(
            ExplanationCopyButton,
            string.IsNullOrWhiteSpace(selectedText) ? _explanationText : selectedText);
    }

    private void ExplanationRecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isExplanationComplete
            || _isExplanationRecordSaving
            || _isExplanationRecorded
            || string.IsNullOrWhiteSpace(_explanationText))
        {
            return;
        }

        if (ManualExplanationRecordRequested is { } requested)
        {
            requested(this);
            return;
        }

        MarkExplanationRecordFailed("unavailable");
    }

    internal int BeginExplanationRecordSave()
    {
        if (!_isExplanationComplete || string.IsNullOrWhiteSpace(_explanationText))
        {
            return 0;
        }

        _explanationRecordVersion++;
        _isExplanationRecordSaving = true;
        _explanationRecordErrorCode = null;
        UpdateExplanationRecordButton();
        return _explanationRecordVersion;
    }

    internal void MarkExplanationRecorded(int version)
    {
        if (version != _explanationRecordVersion)
        {
            return;
        }

        _isExplanationRecordSaving = false;
        _isExplanationRecorded = true;
        _explanationRecordErrorCode = null;
        UpdateExplanationRecordButton();
    }

    internal void MarkExplanationRecordFailed(string? errorCode, int version = 0)
    {
        if (version != 0 && version != _explanationRecordVersion)
        {
            return;
        }

        _isExplanationRecordSaving = false;
        _isExplanationRecorded = false;
        _explanationRecordErrorCode = errorCode;
        UpdateExplanationRecordButton();
    }

    internal bool HasExplanationRecordButtonForVisualTest() =>
        ExplanationRecordButton.Visibility == Visibility.Visible;

    internal bool IsExplanationRecordEnabledForVisualTest() => ExplanationRecordButton.IsEnabled;

    private void UpdateExplanationRecordButton()
    {
        if (ExplanationRecordButton is null)
        {
            return;
        }

        if (_isExplanationRecordSaving)
        {
            ExplanationRecordButton.Content = Localize("Saving…", "保存中…");
            ExplanationRecordButton.ToolTip = Localize(
                "Saving to today's Markdown record",
                "正在保存到今天的 Markdown 记录");
        }
        else if (_isExplanationRecorded)
        {
            ExplanationRecordButton.Content = Localize("Saved", "已保存");
            ExplanationRecordButton.ToolTip = Localize(
                "This explanation is recorded",
                "本次解释已记录");
        }
        else if (_explanationRecordErrorCode is not null)
        {
            ExplanationRecordButton.Content = Localize("Retry", "重试");
            ExplanationRecordButton.ToolTip = _explanationRecordErrorCode switch
            {
                "directory_required" => Localize(
                    "Choose a save folder in Settings, then retry",
                    "请先在设置中选择保存目录，然后重试"),
                "schema_conflict" => Localize(
                    "Today's file is not an Yita record; move or rename it, then retry",
                    "今天的文件不是 Yita 记录，请移动或重命名后重试"),
                _ => Localize(
                    "Could not save the record. Check the folder and retry",
                    "记录保存失败，请检查目录后重试"),
            };
        }
        else
        {
            ExplanationRecordButton.Content = Localize("Record", "记录");
            ExplanationRecordButton.ToolTip = Localize(
                "Save this explanation to today's Markdown record",
                "将本次解释保存到今天的 Markdown 记录");
        }

        ExplanationRecordButton.IsEnabled = _isExplanationComplete
                                            && !string.IsNullOrWhiteSpace(_explanationText)
                                            && !_isExplanationRecordSaving
                                            && !_isExplanationRecorded;
    }

    private void ExplanationRetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastExplanationInvocation is not { } invocation)
        {
            return;
        }

        RequestExplanation(invocation.Scope, invocation.SubjectText);
    }

    private void ExplanationBackButton_Click(object sender, RoutedEventArgs e)
    {
        HideExplanation(notifyDismissed: true);
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPinned)
        {
            IsPinned = false;
            PinButton.Tag = null;
            PinButton.ToolTip = Localize("Keep window", "保留此窗口");
            System.Windows.Automation.AutomationProperties.SetName(
                PinButton,
                Localize("Keep window", "保留此窗口"));
            PinStateChanged?.Invoke(this, false);
            return;
        }

        IsPinned = true;
        EnterManualSizeMode(updateLayout: true);
        PinButton.Tag = "Pinned";
        PinButton.ToolTip = Localize("Release window", "取消保留");
        System.Windows.Automation.AutomationProperties.SetName(
            PinButton,
            Localize("Release window", "取消保留"));
        PinStateChanged?.Invoke(this, true);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void PopupWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var hasControl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (_isEditingTranslation && hasControl && e.Key == Key.Enter)
        {
            TrySaveTranslationCorrection();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_isExplanationVisible)
            {
                HideExplanation(notifyDismissed: true);
            }
            else if (_isEditingTranslation)
            {
                CancelTranslationEditing(showStatus: false);
            }
            else
            {
                Close();
            }

            e.Handled = true;
            return;
        }

        if (!hasControl)
        {
            return;
        }

        if (e.Key == Key.E && !_isEditingTranslation && EditTranslationButton.IsEnabled)
        {
            BeginTranslationEditing();
            e.Handled = true;
        }
        else if (e.Key == Key.P)
        {
            PinButton_Click(PinButton, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key is Key.OemPlus or Key.Add)
        {
            SizePresetBar.FontSizeValue = Math.Min(
                SizePresetBar.MaximumFontSize,
                SizePresetBar.FontSizeValue + 0.5);
            e.Handled = true;
        }
        else if (e.Key is Key.OemMinus or Key.Subtract)
        {
            SizePresetBar.FontSizeValue = Math.Max(
                SizePresetBar.MinimumFontSize,
                SizePresetBar.FontSizeValue - 0.5);
            e.Handled = true;
        }
    }

    private void BeginTranslationEditing()
    {
        SelectReadingView(showSource: false);
        if (!HasTranslation || _isEditingTranslation || !EditTranslationButton.IsEnabled)
        {
            return;
        }

        HideExplanation(notifyDismissed: true);
        _translationBeforeEdit = _translatedText;
        _isEditingTranslation = true;
        UpdateQuestionBarState();
        TranslationRichTextBox.IsReadOnly = false;
        TranslationRichTextBox.IsUndoEnabled = true;
        DirectionButton.IsEnabled = false;
        EditTranslationButton.Tag = "Selected";
        EditTranslationIcon.Visibility = Visibility.Collapsed;
        SaveCorrectionIcon.Visibility = Visibility.Visible;
        ApplyUiLanguage(_uiLanguage);
        ShowActionStatus(ActionMessageKind.CorrectionEditing);
        Activate();
        TranslationRichTextBox.Focus();
        TranslationRichTextBox.CaretPosition = TranslationRichTextBox.Document.ContentEnd;
    }

    private void TrySaveTranslationCorrection()
    {
        if (!_isEditingTranslation)
        {
            return;
        }

        var range = new TextRange(
            TranslationRichTextBox.Document.ContentStart,
            TranslationRichTextBox.Document.ContentEnd);
        var correctedTranslation = range.Text.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(correctedTranslation))
        {
            ShowActionStatus(ActionMessageKind.CorrectionSaveFailed);
            return;
        }

        if (string.Equals(correctedTranslation, _translationBeforeEdit, StringComparison.Ordinal))
        {
            CancelTranslationEditing(showStatus: false);
            return;
        }

        if (CorrectionSaveRequested?.Invoke(this, correctedTranslation) != true)
        {
            ShowActionStatus(ActionMessageKind.CorrectionSaveFailed);
            return;
        }

        FinishTranslationEditing(correctedTranslation);
        ShowActionStatus(ActionMessageKind.CorrectionSaved);
    }

    private void CancelTranslationEditing(bool showStatus)
    {
        if (!_isEditingTranslation)
        {
            return;
        }

        var restoredTranslation = _translationBeforeEdit;
        FinishTranslationEditing(restoredTranslation);
        if (!showStatus)
        {
            HideActionStatus();
        }
    }

    private void FinishTranslationEditing(string translation)
    {
        _isEditingTranslation = false;
        _translationBeforeEdit = string.Empty;
        TranslationRichTextBox.IsReadOnly = true;
        TranslationRichTextBox.IsUndoEnabled = false;
        DirectionButton.IsEnabled = true;
        EditTranslationButton.Tag = null;
        EditTranslationIcon.Visibility = Visibility.Visible;
        SaveCorrectionIcon.Visibility = Visibility.Collapsed;
        _translatedText = translation;
        _lastCompletedTranslation = new CompletedTranslation(
            _currentTranslationSourceText,
            _currentSourceLanguage,
            translation,
            _targetLanguage);
        StopTranslationReveal(clearTarget: true);
        _translationRevealTarget = translation;
        _translationRevealDocument = HighlightMarkup.EnsureEmphasis(
            HighlightedText.FromPlainText(translation));
        _translationParagraph = null;
        _renderedTranslation = string.Empty;
        _renderedTranslationDocument = HighlightedText.Empty;
        SetTranslationText(translation);
        ExplainButton.IsEnabled = HasTranslation;
        CodeAnalysisButton.IsEnabled = HasTranslation;
        _isTranslationComplete = HasTranslation;
        UpdateQuestionBarState();
        ApplyUiLanguage(_uiLanguage);
    }

    private void RequestExplanation(ExplanationScope scope, string? subjectText)
    {
        if (!HasTranslation || _isEditingTranslation)
        {
            return;
        }

        var normalizedSubject = TextNormalizer.Normalize(subjectText);
        if (string.IsNullOrWhiteSpace(normalizedSubject)
            || string.IsNullOrWhiteSpace(_currentTranslationSourceText)
            || string.IsNullOrWhiteSpace(_translatedText))
        {
            return;
        }

        _lastExplanationInvocation = new ExplanationInvocation(normalizedSubject, scope);
        ShowExplanationLoading();
        if (ExplanationRequested is { } requested)
        {
            requested(this, normalizedSubject, scope);
            return;
        }

        ShowExplanationFailure(scope == ExplanationScope.CodeAnalysis
            ? Localize("Code analysis is unavailable. Try again.", "代码分析暂不可用，请重试。")
            : Localize("AI Explain is unavailable. Try again.", "AI 解释暂不可用，请重试。"));
    }

    private bool IsCodeAnalysisActive =>
        _lastExplanationInvocation?.Scope == ExplanationScope.CodeAnalysis;

    private string GetExplanationTitle() => IsCodeAnalysisActive
        ? Localize("Code Analysis", "代码分析")
        : Localize("AI Explain", "AI 解释");

    private string GetExplanationLoadingText() => IsCodeAnalysisActive
        ? Localize("Analyzing code…", "正在分析代码…")
        : Localize("Analyzing…", "正在分析…");

    private void UpdateQuestionBarState()
    {
        if (QuestionBar is null || InlineQuestionTextBox is null || InlineAskButton is null)
        {
            return;
        }

        var isVisible = HasTranslation && _isTranslationComplete && !_isEditingTranslation;
        QuestionBar.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        var isEnabled = isVisible && (!_isExplanationVisible || _isExplanationComplete);
        InlineQuestionTextBox.IsEnabled = isEnabled;
        InlineAskButton.IsEnabled = isEnabled;
    }

    private void SubmitInlineQuestion()
    {
        if (!InlineQuestionTextBox.IsEnabled)
        {
            return;
        }

        var question = TextNormalizer.Normalize(InlineQuestionTextBox.Text);
        if (string.IsNullOrWhiteSpace(question))
        {
            return;
        }

        const int maximumQuestionCharacters = 2000;
        if (question.Length > maximumQuestionCharacters)
        {
            question = question[..maximumQuestionCharacters];
        }

        InlineQuestionTextBox.Clear();
        var contextKind = _isExplanationVisible && _isExplanationComplete
            ? QuestionContextKind.Explanation
            : QuestionContextKind.Translation;
        QuestionSubmitted?.Invoke(this, question, contextKind);
    }

    private void InlineAskButton_Click(object sender, RoutedEventArgs e) => SubmitInlineQuestion();

    private void DeepSeekChatButton_Click(object sender, RoutedEventArgs e)
    {
        DeepSeekChatRequested?.Invoke(this);
    }

    private void InlineQuestionTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (InlineQuestionPlaceholder is not null)
        {
            InlineQuestionPlaceholder.Visibility = string.IsNullOrEmpty(InlineQuestionTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void InlineQuestionTextBox_PreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SubmitInlineQuestion();
        }
    }

    private void TranslationRichTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!CanShowSelectionExplainButton())
        {
            HideSelectionExplainButton();
            return;
        }

        _selectedExplanationText = TextNormalizer.Normalize(TranslationRichTextBox.Selection.Text);
        if (string.IsNullOrWhiteSpace(_selectedExplanationText))
        {
            HideSelectionExplainButton();
            return;
        }

        ScheduleSelectionExplainButton();
    }

    private bool CanShowSelectionExplainButton()
    {
        return HasTranslation
               && !_isEditingTranslation
               && !_isExplanationVisible
               && TranslationRichTextBox.Visibility == Visibility.Visible
               && !TranslationRichTextBox.Selection.IsEmpty;
    }

    private void ScheduleSelectionExplainButton()
    {
        var version = ++_selectionActionVersion;
        Dispatcher.BeginInvoke(
            () =>
            {
                if (!_isClosed && version == _selectionActionVersion)
                {
                    PositionSelectionExplainButton();
                }
            },
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void PositionSelectionExplainButton()
    {
        if (!CanShowSelectionExplainButton())
        {
            HideSelectionExplainButton();
            return;
        }

        var selectedText = TextNormalizer.Normalize(TranslationRichTextBox.Selection.Text);
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            HideSelectionExplainButton();
            return;
        }

        _selectedExplanationText = selectedText;
        TranslationRichTextBox.Margin = new Thickness(0, 0, 0, 36);
        TranslationRichTextBox.UpdateLayout();
        PopupContentGrid.UpdateLayout();
        SelectionActionLayer.UpdateLayout();
        SelectionExplainButton.Visibility = Visibility.Visible;
        SelectionExplainButton.Measure(new System.Windows.Size(
            double.PositiveInfinity,
            double.PositiveInfinity));

        var buttonWidth = Math.Max(SelectionExplainButton.DesiredSize.Width, 62);
        var buttonHeight = Math.Max(SelectionExplainButton.DesiredSize.Height, 28);
        var availableWidth = Math.Max(buttonWidth + 8, SelectionActionLayer.ActualWidth);
        var availableHeight = Math.Max(buttonHeight + 8, SelectionActionLayer.ActualHeight);
        var left = Math.Max(4, availableWidth - buttonWidth - 2);
        var top = Math.Max(4, availableHeight - buttonHeight - 2);

        WpfCanvas.SetLeft(SelectionExplainButton, left);
        WpfCanvas.SetTop(SelectionExplainButton, top);
    }

    private void HideSelectionExplainButton()
    {
        _selectionActionVersion++;
        _selectedExplanationText = string.Empty;
        if (TranslationRichTextBox is not null)
        {
            TranslationRichTextBox.Margin = new Thickness(0);
        }
        if (SelectionExplainButton is not null)
        {
            SelectionExplainButton.Visibility = Visibility.Collapsed;
        }
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !IsVisible)
        {
            return;
        }

        try
        {
            _isUserMoving = true;
            NativeMethods.GetWindowRect(_windowHandle, out var previousBounds);
            if (!IsActive)
            {
                Activate();
            }

            DragMove();
            if (_windowHandle != IntPtr.Zero && NativeMethods.GetWindowRect(_windowHandle, out var bounds)
                && (bounds.Left != previousBounds.Left || bounds.Top != previousBounds.Top))
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                _placementState.MovedTo(new System.Drawing.Point(bounds.Left, bounds.Top), dpi.DpiScaleX, dpi.DpiScaleY);
                PlacementChanged?.Invoke(PreferredPlacement!);
            }
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            // The mouse button may have been released before WPF starts the move loop.
        }
        finally { _isUserMoving = false; }
    }

    private void SizePresetBar_PresetSelected(
        object? sender,
        WindowSizePresetSelectedEventArgs e)
    {
        ApplySizePreset(e.Preset);
    }

    private void ApplySizePreset(WindowSizePreset preset)
    {
        EnterManualSizeMode(updateLayout: true);
        WindowSizePresetCatalog.Apply(this, WindowSizePresetCatalog.ForTranslation(preset));
    }

    private void SetActionBarVisibility(Visibility visibility)
    {
        ActionBarSurface.Visibility = visibility;
        ActionPanel.Visibility = visibility;
    }

    private void PreserveCurrentWindowSize()
    {
        EnterManualSizeMode(updateLayout: true);
    }

    private void EnterManualSizeMode(bool updateLayout)
    {
        if (updateLayout)
        {
            UpdateLayout();
        }

        var currentWidth = ActualWidth > 0 ? ActualWidth : Width;
        var currentHeight = ActualHeight > 0 ? ActualHeight : Height;

        _isAutomaticSizing = false;
        _lastAutomaticSizeTextLength = 0;
        ConfigureResizableLayout(_anchorPoint);
        if (!double.IsNaN(currentWidth) && currentWidth > 0)
        {
            Width = currentWidth;
        }

        if (!double.IsNaN(currentHeight) && currentHeight > 0)
        {
            Height = currentHeight;
        }
    }

    private void ConfigureResizableLayout(ScreenPoint anchorPoint)
    {
        SizeToContent = SizeToContent.Manual;
        MinWidth = PopupAutoSizeCalculator.MinimumWidth;
        MinHeight = PopupAutoSizeCalculator.MinimumHeight;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;
        ContentRow.Height = new GridLength(1, GridUnitType.Star);
        PopupSurface.MaxWidth = double.PositiveInfinity;
        PopupSurface.MaxHeight = double.PositiveInfinity;
        PopupSurface.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        PopupSurface.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
        TranslationRichTextBox.Width = double.NaN;
        TranslationRichTextBox.Height = double.NaN;
        TranslationRichTextBox.MaxHeight = double.PositiveInfinity;
        TranslationRichTextBox.MaxWidth = double.PositiveInfinity;

        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(anchorPoint.X, anchorPoint.Y));
        var dpi = VisualTreeHelper.GetDpi(this);
        MaxWidth = Math.Max(MinWidth, (screen.WorkingArea.Width - 16) / dpi.DpiScaleX);
        MaxHeight = Math.Max(MinHeight, (screen.WorkingArea.Height - 16) / dpi.DpiScaleY);
        PopupSurface.MaxWidth = Math.Max(1, MaxWidth - PopupSurfaceHorizontalInset);
        PopupSurface.MaxHeight = Math.Max(1, MaxHeight - PopupSurfaceVerticalInset);
    }

    private void ApplyAutomaticSize(string text, ScreenPoint anchorPoint, bool force)
    {
        if (!_isAutomaticSizing || IsPinned || _isUserMoving || _hasUserMoved)
        {
            return;
        }

        if (!force
            && IsVisible
            && text.Length < _lastAutomaticSizeTextLength + AutomaticSizeGrowthThreshold)
        {
            return;
        }

        _lastAutomaticSizeTextLength = text.Length;
        ConfigureResizableLayout(anchorPoint);
        HeaderBarGrid.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var surfaceMaximumWidth = Math.Max(1, MaxWidth - PopupSurfaceHorizontalInset);
        var surfaceMaximumHeight = Math.Max(1, MaxHeight - PopupSurfaceVerticalInset);
        var targetSize = PopupAutoSizeCalculator.Calculate(
            text,
            SizePresetBar.FontSizeValue,
            surfaceMaximumWidth,
            surfaceMaximumHeight,
            Math.Max(0, HeaderBarGrid.DesiredSize.Width - PopupSurfaceHorizontalInset),
            TranslationTextSurface.Padding.Left + TranslationTextSurface.Padding.Right,
            TranslationTextSurface.Padding.Top + TranslationTextSurface.Padding.Bottom);

        var targetWidth = targetSize.Width + PopupSurfaceHorizontalInset;
        // Automatic mode owns the size. Recompute both directions as streaming
        // text grows or the view switches; manual resize/move exits this mode.
        var targetHeight = targetSize.Height + PopupSurfaceVerticalInset;
        var sizeChanged = Math.Abs(Width - targetWidth) > 0.5
                          || Math.Abs(Height - targetHeight) > 0.5;
        Width = targetWidth;
        Height = targetHeight;

        if (sizeChanged && IsVisible)
        {
            SchedulePositionNearAnchor();
        }
    }

    private async Task CopyWithFeedbackAsync(WpfButton button, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            WpfClipboard.SetText(text);
            button.ToolTip = Localize("Copied", "已复制");
            ShowActionStatus(ActionMessageKind.CopySucceeded);
        }
        catch (ExternalException)
        {
            button.ToolTip = Localize("Copy failed", "复制失败");
            ShowActionStatus(ActionMessageKind.CopyFailed);
        }

        var feedbackVersion = ++_copyFeedbackVersion;
        try
        {
            await Task.Delay(800, _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        if (feedbackVersion == _copyFeedbackVersion)
        {
            ApplyCopyButtonToolTips();
            HideActionStatus();
        }
    }

    private void SetTranslationText(string text)
    {
        var fontSize = SizePresetBar is null || double.IsNaN(SizePresetBar.FontSizeValue)
            ? DefaultTranslationFontSize
            : SizePresetBar.FontSizeValue;
        var highlightedText = _translationRevealDocument.Prefix(text.Length);
        if (_translationParagraph is not null
            && string.Equals(text, _renderedTranslation, StringComparison.Ordinal)
            && _renderedTranslationDocument.HasSamePresentation(highlightedText))
        {
            return;
        }

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            LineHeight = CalculateLineHeight(fontSize),
        };

        HighlightedTextRenderer.AppendTranslationRuns(
            paragraph,
            highlightedText,
            _englishTranslationFont,
            _chineseTranslationFont);

        TranslationRichTextBox.Document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,
            ColumnGap = 0,
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = TranslationRichTextBox.Foreground,
            FontFamily = _englishTranslationFont,
            FontSize = fontSize,
            TextAlignment = TextAlignment.Left,
        };
        _translationParagraph = paragraph;
        _renderedTranslation = text;
        _renderedTranslationDocument = highlightedText;
        ApplyTranslationFontSize(fontSize);
    }

    private void SetExplanationText(string text)
    {
        var fontSize = SizePresetBar is null || double.IsNaN(SizePresetBar.FontSizeValue)
            ? DefaultTranslationFontSize
            : SizePresetBar.FontSizeValue;
        var highlightedText = _explanationRevealDocument.Prefix(text.Length);
        if (_explanationParagraph is not null
            && string.Equals(text, _renderedExplanation, StringComparison.Ordinal)
            && _renderedExplanationDocument.HasSamePresentation(highlightedText))
        {
            return;
        }

        var paragraph = new Paragraph()
        {
            Margin = new Thickness(0),
            LineHeight = CalculateLineHeight(fontSize),
            FontFamily = _chineseTranslationFont,
        };
        HighlightedTextRenderer.AppendUniformRuns(
            paragraph,
            highlightedText,
            _chineseTranslationFont);

        ExplanationRichTextBox.Document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,
            ColumnGap = 0,
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = ExplanationRichTextBox.Foreground,
            FontFamily = _chineseTranslationFont,
            FontSize = fontSize,
            TextAlignment = TextAlignment.Left,
        };
        _explanationParagraph = paragraph;
        _renderedExplanation = text;
        _renderedExplanationDocument = highlightedText;
        ApplyExplanationFontSize(fontSize);
    }

    private void QueueTranslationReveal(HighlightedText document, bool resetVisibleText)
    {
        var text = document.PlainText;
        var emphasisChanged = !_translationRevealDocument.HasSamePresentation(document);
        var needsReset = resetVisibleText
            || !text.StartsWith(_translationRevealTarget, StringComparison.Ordinal)
            || _renderedTranslation.Length > text.Length
            || !text.StartsWith(_renderedTranslation, StringComparison.Ordinal);
        _translationRevealTarget = text;
        _translationRevealDocument = document;

        if (needsReset)
        {
            _translationRevealTimer.Stop();
            _completeTranslationAfterReveal = false;
            _translationParagraph = null;
            _renderedTranslation = string.Empty;
            _renderedTranslationDocument = HighlightedText.Empty;

            var initialEnd = AdvanceRevealIndex(text, 0, StreamingRevealInitialCharacters);
            SetTranslationText(text[..initialEnd]);
        }
        else if (emphasisChanged)
        {
            SetTranslationText(_renderedTranslation);
        }

        EnsureTranslationRevealTimer();
    }

    private void QueueExplanationReveal(HighlightedText document, bool resetVisibleText)
    {
        var text = document.PlainText;
        var emphasisChanged = !_explanationRevealDocument.HasSamePresentation(document);
        var needsReset = resetVisibleText
            || !text.StartsWith(_explanationRevealTarget, StringComparison.Ordinal)
            || _renderedExplanation.Length > text.Length
            || !text.StartsWith(_renderedExplanation, StringComparison.Ordinal);
        _explanationRevealTarget = text;
        _explanationRevealDocument = document;

        if (needsReset)
        {
            _explanationRevealTimer.Stop();
            _explanationParagraph = null;
            _renderedExplanation = string.Empty;
            _renderedExplanationDocument = HighlightedText.Empty;

            var initialEnd = AdvanceRevealIndex(text, 0, StreamingRevealInitialCharacters);
            SetExplanationText(text[..initialEnd]);
        }
        else if (emphasisChanged)
        {
            SetExplanationText(_renderedExplanation);
        }

        EnsureExplanationRevealTimer();
    }

    private void TranslationRevealTimer_Tick(object? sender, EventArgs e)
        => RunRevealFrame(AdvanceTranslationReveal);

    private void AdvanceTranslationReveal()
    {
        if (_renderedTranslation.Length >= _translationRevealTarget.Length)
        {
            FinishTranslationReveal();
            return;
        }

        var nextEnd = GetNextRevealEnd(_translationRevealTarget, _renderedTranslation.Length);
        SetTranslationText(_translationRevealTarget[..nextEnd]);
        if (nextEnd >= _translationRevealTarget.Length)
        {
            FinishTranslationReveal();
        }
    }

    private void ExplanationRevealTimer_Tick(object? sender, EventArgs e)
        => RunRevealFrame(AdvanceExplanationReveal);

    private void AdvanceExplanationReveal()
    {
        if (_renderedExplanation.Length >= _explanationRevealTarget.Length)
        {
            _explanationRevealTimer.Stop();
            return;
        }

        var nextEnd = GetNextRevealEnd(_explanationRevealTarget, _renderedExplanation.Length);
        SetExplanationText(_explanationRevealTarget[..nextEnd]);
        if (nextEnd >= _explanationRevealTarget.Length)
        {
            _explanationRevealTimer.Stop();
        }
    }

    private void EnsureTranslationRevealTimer()
    {
        if (_renderedTranslation.Length < _translationRevealTarget.Length
            && !_translationRevealTimer.IsEnabled)
        {
            _translationRevealTimer.Start();
        }
    }

    internal void RunRevealFrame(Action render)
    {
        if (_isClosed) return;
        try { render(); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException
                                         or System.Runtime.InteropServices.ExternalException)
        {
            // DispatcherTimer frames run outside the coordinator's awaited request.
            // Abandon this popup on a presentation failure instead of terminating the app.
            _translationRevealTimer.Stop();
            _explanationRevealTimer.Stop();
            RenderingFailed?.Invoke(exception);
            Close();
        }
    }

    private void EnsureExplanationRevealTimer()
    {
        if (_renderedExplanation.Length < _explanationRevealTarget.Length
            && !_explanationRevealTimer.IsEnabled)
        {
            _explanationRevealTimer.Start();
        }
    }

    private void FinishTranslationReveal()
    {
        _translationRevealTimer.Stop();
        if (_completeTranslationAfterReveal)
        {
            CompleteTranslationPresentation();
        }
    }

    private void StopTranslationReveal(bool clearTarget)
    {
        _translationRevealTimer.Stop();
        _completeTranslationAfterReveal = false;
        if (clearTarget)
        {
            _translationRevealTarget = string.Empty;
            _translationRevealDocument = HighlightedText.Empty;
        }
    }

    private void StopExplanationReveal(bool clearTarget)
    {
        _explanationRevealTimer.Stop();
        if (clearTarget)
        {
            _explanationRevealTarget = string.Empty;
            _explanationRevealDocument = HighlightedText.Empty;
        }
    }

    private static int GetNextRevealEnd(string text, int renderedLength)
    {
        var remainingCharacters = Math.Max(0, text.Length - renderedLength);
        var characterBudget = Math.Clamp(
            (int)Math.Ceiling(remainingCharacters / (double)StreamingRevealTargetFrames),
            StreamingRevealMinimumCharactersPerFrame,
            StreamingRevealMaximumCharactersPerFrame);
        return AdvanceRevealIndex(text, renderedLength, characterBudget);
    }

    private static int AdvanceRevealIndex(string text, int startIndex, int characterBudget)
    {
        var index = Math.Clamp(startIndex, 0, text.Length);
        var remainingBudget = Math.Max(1, characterBudget);
        while (index < text.Length && remainingBudget-- > 0)
        {
            if (char.IsHighSurrogate(text[index])
                && index + 1 < text.Length
                && char.IsLowSurrogate(text[index + 1]))
            {
                index += 2;
            }
            else
            {
                index++;
            }
        }

        return index;
    }

    private void ShowActionStatus(ActionMessageKind messageKind)
    {
        _actionMessageKind = messageKind;
        UpdateActionMessageText();
        ActionStatusText.Visibility = Visibility.Visible;
    }

    private void HideActionStatus()
    {
        _actionMessageKind = ActionMessageKind.None;
        ActionStatusText.Visibility = Visibility.Collapsed;
    }

    private void UpdateDirectionButtonText()
    {
        var nextTarget = LanguageDirectionResolver.GetOppositeTarget(_targetLanguage);
        DirectionButton.Content = nextTarget == LanguageDirectionResolver.English
            ? Localize("To English", "译为英文")
            : Localize("To Chinese", "译为中文");
    }

    private void UpdateBodyMessageText()
    {
        LoadingTextBlock.Text = _bodyMessageKind switch
        {
            BodyMessageKind.Failure => Localize("Translation failed. Try again.", "翻译失败，请重试"),
            BodyMessageKind.CustomFailure => _customFailureMessage
                ?? Localize("Translation failed. Try again.", "翻译失败，请重试"),
            _ => Localize("Translating…", "正在翻译…"),
        };
    }

    private void UpdateActionMessageText()
    {
        ActionStatusText.Text = _actionMessageKind switch
        {
            ActionMessageKind.Retranslating => Localize("Retranslating…", "正在重新翻译…"),
            ActionMessageKind.CopySucceeded => Localize("✓ Copied", "✓ 已复制"),
            ActionMessageKind.CopyFailed => Localize("Copy failed", "复制失败"),
            ActionMessageKind.TranslationFailedPreserved => Localize(
                "Translation failed · Previous result kept",
                "翻译失败 · 已保留原译文"),
            ActionMessageKind.CorrectionEditing => Localize(
                "Editing · Ctrl+Enter to save · Esc to cancel",
                "正在编辑 · Ctrl+Enter 保存 · Esc 取消"),
            ActionMessageKind.CorrectionSaved => Localize(
                "✓ Saved to translation memory",
                "✓ 已保存到翻译记忆"),
            ActionMessageKind.CorrectionSaveFailed => Localize(
                "Could not save correction",
                "无法保存修正译文"),
            _ => string.Empty,
        };
    }

    private void ApplyCopyButtonToolTips()
    {
        CopySourceButton.ToolTip = Localize("Copy source", "复制原文");
        CopyTranslationButton.ToolTip = Localize("Copy translation", "复制译文");
        ExplanationCopyButton.ToolTip = Localize("Copy explanation", "复制解释");
    }

    private string Localize(string english, string chinese)
    {
        return _uiLanguage == "zh-CN" ? chinese : english;
    }

    private static System.Windows.Media.FontFamily CreateFontFamily(string? requested, string fallback)
    {
        var familyName = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
        if (familyName.Equals("Source Sans Pro", StringComparison.OrdinalIgnoreCase)
            && System.Windows.Application.Current?.TryFindResource("BundledSourceSansFont")
                is System.Windows.Media.FontFamily bundledSourceSans)
        {
            return bundledSourceSans;
        }

        try
        {
            return new System.Windows.Media.FontFamily(familyName);
        }
        catch (ArgumentException)
        {
            return new System.Windows.Media.FontFamily(fallback);
        }
    }

    private static Geometry CreateFrozenGeometry(string pathData)
    {
        var geometry = Geometry.Parse(pathData);
        geometry.Freeze();
        return geometry;
    }

    private void SizePresetBar_FontSizeChanged(object? sender, WindowFontSizeChangedEventArgs e)
    {
        if (double.IsNaN(e.NewValue) || e.NewValue <= 0)
        {
            return;
        }

        ApplyTranslationFontSize(e.NewValue);
        if (_isAutomaticSizing && _translatedText.Length > 0)
        {
            ApplyAutomaticSize(_translatedText, _anchorPoint, force: true);
        }
    }

    private void ApplyTranslationFontSize(double fontSize)
    {
        if (TranslationRichTextBox is null)
        {
            return;
        }

        TranslationRichTextBox.FontSize = fontSize;
        if (SourceRichTextBox is not null)
        {
            SourceRichTextBox.FontSize = fontSize;
            SourceRichTextBox.Document.FontSize = fontSize;
            foreach (var paragraph in SourceRichTextBox.Document.Blocks.OfType<Paragraph>())
                paragraph.LineHeight = CalculateLineHeight(fontSize);
        }
        if (TranslationRichTextBox.Document is not { } document)
        {
            return;
        }

        document.FontSize = fontSize;
        var lineHeight = CalculateLineHeight(fontSize);
        foreach (var block in document.Blocks)
        {
            if (block is Paragraph paragraph)
            {
                paragraph.LineHeight = lineHeight;
            }
        }

        ApplyExplanationFontSize(fontSize);
    }

    private void ApplyExplanationFontSize(double fontSize)
    {
        if (ExplanationRichTextBox is null)
        {
            return;
        }

        ExplanationRichTextBox.FontFamily = _chineseTranslationFont;
        ExplanationRichTextBox.FontSize = fontSize;
        if (ExplanationRichTextBox.Document is not { } document)
        {
            return;
        }

        document.FontFamily = _chineseTranslationFont;
        document.FontSize = fontSize;
        var lineHeight = CalculateLineHeight(fontSize);
        foreach (var block in document.Blocks)
        {
            if (block is Paragraph paragraph)
            {
                paragraph.LineHeight = lineHeight;
            }
        }
    }

    private static double CalculateLineHeight(double fontSize)
    {
        return Math.Round(fontSize * 1.55, 2);
    }

    private void TranslationRichTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HideSelectionExplainButton();
        if (!IsActive)
        {
            Activate();
        }

        TranslationRichTextBox.Focus();
    }

    private TextPointer? GetTranslationPointerAtOffset(int characterOffset)
    {
        if (_translationParagraph is null)
        {
            return null;
        }

        var remaining = characterOffset;
        foreach (var inline in _translationParagraph.Inlines)
        {
            if (inline is not Run run)
            {
                continue;
            }

            var runLength = run.Text.Length;
            if (remaining <= runLength)
            {
                return run.ContentStart.GetPositionAtOffset(remaining, LogicalDirection.Forward)
                    ?? run.ContentEnd;
            }

            remaining -= runLength;
        }

        return _translationParagraph.ContentEnd;
    }

    private void PopupWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePopupSurfaceClip();

        if (!IsVisible
            || IsPinned
            || SizeToContent == SizeToContent.Manual
            || _isRepositionPending)
        {
            return;
        }

        SchedulePositionNearAnchor();
    }

    private void PopupSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePopupSurfaceClip();
    }

    private void UpdatePopupSurfaceClip()
    {
        var width = PopupSurface.ActualWidth;
        var height = PopupSurface.ActualHeight;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return;
        }

        var requestedRadius = Math.Max(
            Math.Max(PopupSurface.CornerRadius.TopLeft, PopupSurface.CornerRadius.TopRight),
            Math.Max(PopupSurface.CornerRadius.BottomLeft, PopupSurface.CornerRadius.BottomRight));
        var radius = Math.Min(requestedRadius, Math.Min(width, height) / 2);
        var clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
        clip.Freeze();
        PopupSurface.Clip = clip;
    }

    private void SchedulePositionNearAnchor()
    {
        if (_isRepositionPending)
        {
            return;
        }

        _isRepositionPending = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _isRepositionPending = false;
                if (!_isClosed && IsVisible && !IsPinned)
                {
                    UpdateLayout();
                    PositionNear(_anchorPoint);
                }
            },
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private void PositionNear(ScreenPoint anchorPoint)
    {
        if (_isUserMoving || _hasUserMoved) return;
        if (_windowHandle == IntPtr.Zero)
        {
            _windowHandle = new WindowInteropHelper(this).EnsureHandle();
        }

        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(anchorPoint.X, anchorPoint.Y));
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        if (NativeMethods.GetWindowRect(_windowHandle, out var currentBounds)
            && currentBounds.Width > 0
            && currentBounds.Height > 0)
        {
            width = currentBounds.Width;
            height = currentBounds.Height;
        }

        const int screenMargin = 8;

        width = Math.Min(width, Math.Max(1, screen.WorkingArea.Width - (screenMargin * 2)));
        height = Math.Min(height, Math.Max(1, screen.WorkingArea.Height - (screenMargin * 2)));

        var position = PopupPlacement.Resolve(anchorPoint, new System.Drawing.Size(width, height),
            screen.WorkingArea, dpi.DpiScaleX, dpi.DpiScaleY, PreferredPlacement);
        var x = position.X;
        var y = position.Y;

        UpdateBubbleTailDirection(windowIsLeftOfAnchor: x < anchorPoint.X);

        NativeMethods.SetWindowPos(
            _windowHandle,
            NativeMethods.HwndTopmost,
            x,
            y,
            width,
            height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private enum ActionMessageKind
    {
        None,
        Retranslating,
        CopySucceeded,
        CopyFailed,
        TranslationFailedPreserved,
        CorrectionEditing,
        CorrectionSaved,
        CorrectionSaveFailed,
    }

    private enum BodyMessageKind
    {
        Translating,
        Failure,
        CustomFailure,
    }

    private readonly record struct CompletedTranslation(
        string SourceText,
        string SourceLanguage,
        string TranslatedText,
        string TargetLanguage);

    private readonly record struct ExplanationInvocation(
        string SubjectText,
        ExplanationScope Scope);
}
