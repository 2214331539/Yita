using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Yita.Core;
using Yita.Core.Selection;
using Yita.Core.Placement;
using Yita.Settings;
using Yita.Translation;
using Yita.Windows;

namespace Yita.Desktop;

public sealed partial class TranslationPopupWindow : Window
{
    private bool _dragging;
    private PixelPoint _dragStartPoint;
    private PixelPoint _dragStartPosition;
    private PopupOffset? _preferredOffset;
    private string _sourceText = string.Empty;
    private string _translatedText = "正在翻译…";
    private bool _showOriginal;
    private bool _hasError;
    private bool _userSized;
    private bool _hasAnchor;
    private AppSettings _settings = AppSettings.Default;
    private ExplanationAction? _lastExplanation;
    private bool _explanationComplete;
    private int _explanationVersion;
    private int _recordedExplanationVersion;
    private bool _savingExplanation;

    public TranslationPopupWindow()
    {
        InitializeComponent();
        ReferenceTheme.Apply(Resources, _settings);
        SizeChanged += (_, _) => ConstrainToScreen();
        Opened += (_, _) => ReferenceMotion.Reveal(PopupRoot, scale: true);
        Closed += (_, _) => { TranslationRequests.Dispose(); ExplanationRequests.Dispose(); };
        TranslationText.AddHandler(PointerReleasedEvent, ReadingPointerReleased, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
    }

    public event EventHandler<PopupMovedEventArgs>? Moved;
    public event EventHandler? Dismissed;
    public SelectionAnchor CurrentAnchor { get; private set; }
    public bool IsPinned => PinButton.IsChecked == true;
    internal LatestRequestController TranslationRequests { get; } = new();
    internal LatestRequestController ExplanationRequests { get; } = new();
    internal event EventHandler<ExplanationAction>? ExplanationRequested;
    internal event EventHandler<QuestionAction>? QuestionRequested;
    internal event EventHandler<string>? CorrectionSaved;
    internal event EventHandler? RecordExplanationRequested;

    internal void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        ReferenceTheme.Apply(Resources, settings);
        var chinese = settings.UiLanguage == "zh-CN";
        OriginalButton.Content = chinese ? "原文" : "Source";
        TranslatedButton.Content = chinese ? "译文" : "Translation";
        ToolTip.SetTip(PinButton, chinese ? "固定窗口" : "Keep window");
        ToolTip.SetTip(CloseButton, chinese ? "关闭翻译" : "Close");
        ToolTip.SetTip(DragHandle, chinese ? "拖动窗口" : "Drag to move");
        InlineQuestionTextBox.Watermark = chinese ? "对此提问…" : "Ask about this…";
        AskButton.Content = chinese ? "提问" : "Ask";
        ExplanationTitle.Text = chinese ? "AI 解释" : "AI explanation";
        ExplanationCopyButton.Content = chinese ? "复制" : "Copy";
        ExplanationRecordButton.Content = chinese ? "记录" : "Record";
        ExplanationRetryButton.Content = chinese ? "重试" : "Retry";
        ExplanationBackButton.Content = chinese ? "返回" : "Back";
        CopyTextMenu.Header = chinese ? "复制" : "Copy";
        SelectAllMenu.Header = chinese ? "全选" : "Select all";
        ExplainMenu.Header = chinese ? "解释" : "Explain";
        SelectionExplainButton.Content = chinese ? "解释" : "Explain";
        CodeMenu.Header = chinese ? "分析代码" : "Analyze code";
        EditMenu.Header = chinese ? "修改并保存译文" : "Edit and save correction";
        SaveCorrectionButton.Content = chinese ? "保存" : "Save";
        CancelCorrectionButton.Content = chinese ? "取消" : "Cancel";
        TranslationText.FontSize = ExplanationText.FontSize = settings.DefaultTranslationFontSize;
        TranslationText.LineHeight = ExplanationText.LineHeight = Math.Round(settings.DefaultTranslationFontSize * 1.55, 2);
        TranslationText.FontFamily = ExplanationText.FontFamily = new FontFamily(settings.EnglishTranslationFontFamily);
        BubbleTail.IsVisible = PopupVisualStyleCatalog.IsBubble(settings.PopupVisualStyle);
        var sculpted = PopupVisualStyleCatalog.IsBubbleV2(settings.PopupVisualStyle) || PopupVisualStyleCatalog.IsBubbleV3(settings.PopupVisualStyle);
        PopupSurface.Margin = BubbleTail.IsVisible ? new Thickness(sculpted ? 14 : 12, sculpted ? 4 : 3) : new Thickness(0);
        BubbleTail.Data = Geometry.Parse(sculpted ? "M0,8 C4,5 8,2 16,0 C13,5 13,11 16,16 C8,14 4,11 0,8 Z" : "M0,0 L18,8 L0,16 Z");
    }

    public void BeginTranslation(string source)
    {
        _sourceText = source;
        _translatedText = _settings.UiLanguage == "zh-CN" ? "正在翻译…" : "Translating…";
        _hasError = false;
        _showOriginal = false;
        _userSized = false;
        ExplanationRequests.Cancel();
        _explanationVersion++;
        _savingExplanation = false;
        _explanationComplete = false;
        ExplanationOverlay.IsVisible = CorrectionPanel.IsVisible = QuestionRow.IsVisible = NoticeText.IsVisible = false;
        LoadingText.Text = _translatedText;
        LoadingPanel.IsVisible = true;
        ReadingScroll.IsVisible = false;
        InlineQuestionTextBox.Text = "";
        SelectionExplainButton.IsVisible = false;
        ReadingScroll.Offset = default;
        RefreshText();
    }

    public void SetText(string text)
    {
        _translatedText = text;
        _hasError = false;
        LoadingPanel.IsVisible = false;
        ReadingScroll.IsVisible = true;
        RefreshText();
    }

    public void SetError(string text)
    {
        _translatedText = text;
        _hasError = true;
        LoadingPanel.IsVisible = false;
        ReadingScroll.IsVisible = true;
        QuestionRow.IsVisible = false;
        RefreshText();
    }

    internal void CompleteTranslation() { QuestionRow.IsVisible = true; RefreshText(); }

    public void PlaceNear(SelectionRequest request, SelectionResult result, PopupOffset? savedOffset = null)
    {
        var anchor = result.Bounds is { IsValid: true, Width: > 0, Height: > 0 } bounds
            ? bounds.LowerLeft : request.GestureBounds?.LowerLeft ?? request.Pointer;
        if (!anchor.IsFinite) return;
        CurrentAnchor = new SelectionAnchor(anchor.X, anchor.Y);
        _hasAnchor = true;
        _preferredOffset = savedOffset;
        var screen = Screens.ScreenFromPoint(new PixelPoint((int)anchor.X, (int)anchor.Y)) ?? Screens.Primary;
        if (screen is not null)
        {
            MaxWidth = Math.Min(820, screen.WorkingArea.Width / screen.Scaling - 16);
            MaxHeight = Math.Min(600, screen.WorkingArea.Height / screen.Scaling - 16);
            MinWidth = Math.Min(380, MaxWidth);
            MinHeight = Math.Min(192, MaxHeight);
            Width = Math.Clamp(Width, MinWidth, MaxWidth);
        }
        PlaceAtPreferredOffset();
    }

    private void RefreshText()
    {
        ReferenceTypography.SetText(TranslationText, _showOriginal ? _sourceText : _translatedText, _settings);
        TranslationText.Foreground = (IBrush)Resources[!_showOriginal && _hasError ? "DangerBrush" : "PopupTextBrush"]!;
        OriginalButton.Classes.Set("selected", _showOriginal);
        TranslatedButton.Classes.Set("selected", !_showOriginal);
        EditMenu.IsEnabled = !_showOriginal && !_hasError && QuestionRow.IsVisible;
        if (_userSized) return;
        var content = ExplanationOverlay.IsVisible ? ExplanationText : TranslationText;
        var glass = PopupVisualStyleCatalog.IsBubbleV3(_settings.PopupVisualStyle);
        var calculated = PopupAutoSizeCalculator.Calculate(ReferenceTypography.GetText(content), content.FontSize, MaxWidth, MaxHeight,
            additionalHorizontalPadding: glass ? 24 : 0, additionalVerticalPadding: glass ? 24 : 0);
        var inset = PopupSurface.Margin;
        Width = Math.Clamp(calculated.Width + inset.Left + inset.Right, MinWidth, MaxWidth);
        content.Measure(new Size(Math.Max(100, Width - 44 - inset.Left - inset.Right - (glass ? 24 : 0)), double.PositiveInfinity));
        Height = Math.Clamp(Math.Max(calculated.Height, content.DesiredSize.Height + 174 + (glass ? 24 : 0)) + inset.Top + inset.Bottom, MinHeight, MaxHeight);
        ConstrainToScreen();
    }

    private void PlaceAtPreferredOffset()
    {
        if (!_hasAnchor) return;
        var screen = Screens.ScreenFromPoint(new PixelPoint((int)CurrentAnchor.X, (int)CurrentAnchor.Y)) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var position = PopupPlacementResolver.ResolveForScale(CurrentAnchor, new PopupSize(Width, Height),
            new WorkArea(area.X, area.Y, area.Width, area.Height), screen.Scaling, _preferredOffset);
        Position = new PixelPoint((int)Math.Round(position.X), (int)Math.Round(position.Y));
    }

    private void ConstrainToScreen()
    {
        if (!IsVisible || _dragging) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var size = new PixelSize((int)Math.Ceiling(Width * screen.Scaling), (int)Math.Ceiling(Height * screen.Scaling));
        Position = new PixelPoint(
            Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - size.Width)),
            Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height)));
    }

    private void HeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && (source is Button || source is ToggleButton
            || source.GetVisualAncestors().Any(visual => visual is Button || visual is ToggleButton))) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _dragStartPoint = this.PointToScreen(e.GetPosition(this));
        _dragStartPosition = Position;
        e.Pointer.Capture(sender as IInputElement ?? this);
        e.Handled = true;
    }

    private void HeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        var point = this.PointToScreen(e.GetPosition(this));
        Position = new PixelPoint(_dragStartPosition.X + point.X - _dragStartPoint.X,
            _dragStartPosition.Y + point.Y - _dragStartPoint.Y);
        e.Handled = true;
    }

    private void HeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        FinishDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void HeaderPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => FinishDrag();

    private void FinishDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        ConstrainToScreen();
        var screen = Screens.ScreenFromWindow(this);
        var offset = PopupPlacementResolver.CaptureLogicalOffset(CurrentAnchor, Position.X, Position.Y,
            screen?.Scaling ?? RenderScaling);
        if (!offset.IsValid) return;
        _preferredOffset = offset;
        Moved?.Invoke(this, new PopupMovedEventArgs(Position, offset));
    }

    private void ResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _userSized = true;
        BeginResizeDrag(WindowEdge.SouthEast, e);
        e.Handled = true;
    }

    private void OriginalClick(object? sender, RoutedEventArgs e) { _showOriginal = true; RefreshText(); }
    private void TranslatedClick(object? sender, RoutedEventArgs e) { _showOriginal = false; RefreshText(); }
    private void PinClick(object? sender, RoutedEventArgs e) => Topmost = true;

    internal void BeginExplanation()
    {
        _explanationVersion++;
        _savingExplanation = false;
        _explanationComplete = false;
        ExplanationRecordButton.Content = _settings.UiLanguage == "zh-CN" ? "记录" : "Record";
        SelectionExplainButton.IsVisible = false;
        ExplanationOverlay.IsVisible = true;
        ExplanationText.Text = _settings.UiLanguage == "zh-CN" ? "正在解释…" : "Explaining…";
        ExplanationRecordButton.IsEnabled = false;
        ExplanationRetryButton.IsVisible = false;
        RefreshText();
    }

    private void ReadingPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var point = e.GetPosition(SelectionActionLayer);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SelectionExplainButton.IsVisible = !string.IsNullOrWhiteSpace(TranslationText.SelectedText)
                && !_hasError && !ExplanationOverlay.IsVisible && QuestionRow.IsVisible;
            if (!SelectionExplainButton.IsVisible) return;
            Canvas.SetLeft(SelectionExplainButton, Math.Clamp(point.X + 6, 0, Math.Max(0, SelectionActionLayer.Bounds.Width - 76)));
            Canvas.SetTop(SelectionExplainButton, Math.Clamp(point.Y - 34, 0, Math.Max(0, SelectionActionLayer.Bounds.Height - 30)));
        });
    }

    internal void SetExplanation(string text) { ReferenceTypography.SetText(ExplanationText, text, _settings); RefreshText(); }
    internal void CompleteExplanation() { _explanationComplete = true; ExplanationRecordButton.IsEnabled = true; }
    internal int BeginExplanationRecord()
    {
        if (!_explanationComplete || _savingExplanation || _recordedExplanationVersion == _explanationVersion) return 0;
        _savingExplanation = true;
        ExplanationRecordButton.IsEnabled = false;
        ExplanationRecordButton.Content = _settings.UiLanguage == "zh-CN" ? "保存中…" : "Saving…";
        return _explanationVersion;
    }
    internal bool CompleteExplanationRecord(int version, bool saved)
    {
        if (version != _explanationVersion) return false;
        _savingExplanation = false;
        if (saved) _recordedExplanationVersion = version;
        ExplanationRecordButton.IsEnabled = !saved && _explanationComplete;
        ExplanationRecordButton.Content = saved ? (_settings.UiLanguage == "zh-CN" ? "已保存" : "Saved")
            : (_settings.UiLanguage == "zh-CN" ? "重试" : "Retry");
        return true;
    }
    internal void SetExplanationError(string text) { SetExplanation(text); ExplanationRetryButton.IsVisible = true; }
    internal void ShowNotice(string text) { NoticeText.Text = text; NoticeText.IsVisible = true; }

    private string SelectedSubject() => string.IsNullOrWhiteSpace(TranslationText.SelectedText)
        ? _showOriginal ? _sourceText : _translatedText : TranslationText.SelectedText;

    private void ExplainClick(object? sender, RoutedEventArgs e) => RequestExplanation(new ExplanationAction(SelectedSubject(),
        _showOriginal ? ExplanationScope.SourceText : ExplanationScope.TranslationSelection));
    private void CodeClick(object? sender, RoutedEventArgs e) => RequestExplanation(new ExplanationAction(SelectedSubject(), ExplanationScope.CodeAnalysis));
    private void RequestExplanation(ExplanationAction action) { _lastExplanation = action; ExplanationRequested?.Invoke(this, action); }
    private void RetryExplanationClick(object? sender, RoutedEventArgs e) { if (_lastExplanation is { } action) RequestExplanation(action); }
    private void BackExplanationClick(object? sender, RoutedEventArgs e) { ExplanationRequests.Cancel(); ExplanationOverlay.IsVisible = false; RefreshText(); }
    private void RecordExplanationClick(object? sender, RoutedEventArgs e) => RecordExplanationRequested?.Invoke(this, EventArgs.Empty);
    private async void CopyExplanationClick(object? sender, RoutedEventArgs e) => await CopyAsync(ReferenceTypography.GetText(ExplanationText));
    private async void CopyReadingClick(object? sender, RoutedEventArgs e) => await CopyAsync(SelectedSubject());
    private void SelectAllClick(object? sender, RoutedEventArgs e) => TranslationText.SelectAll();
    private async Task CopyAsync(string text)
    {
        try { if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text); }
        catch { ShowNotice(_settings.UiLanguage == "zh-CN" ? "无法复制。" : "Could not copy."); }
    }

    private void EditClick(object? sender, RoutedEventArgs e)
    {
        CorrectionText.Text = _translatedText;
        CorrectionPanel.IsVisible = true;
        CorrectionText.Focus();
    }
    private void CancelCorrectionClick(object? sender, RoutedEventArgs e) => CorrectionPanel.IsVisible = false;
    private void SaveCorrectionClick(object? sender, RoutedEventArgs e)
    {
        var text = CorrectionText.Text?.Trim() ?? "";
        if (text.Length == 0) return;
        CorrectionSaved?.Invoke(this, text);
        CorrectionPanel.IsVisible = false;
    }
    private void AskClick(object? sender, RoutedEventArgs e)
    {
        var question = InlineQuestionTextBox.Text?.Trim() ?? "";
        if (question.Length == 0) return;
        QuestionRequested?.Invoke(this, new QuestionAction(question, ExplanationOverlay.IsVisible && _explanationComplete
            ? QuestionContextKind.Explanation : QuestionContextKind.Translation));
        InlineQuestionTextBox.Text = "";
    }
    private void ChatClick(object? sender, RoutedEventArgs e) => QuestionRequested?.Invoke(this, new QuestionAction("", QuestionContextKind.GeneralChat));
    private void QuestionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        AskClick(sender, new RoutedEventArgs());
        e.Handled = true;
    }
    private void PopupKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.E && EditMenu.IsEnabled)
        { EditClick(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Enter && CorrectionPanel.IsVisible)
        { SaveCorrectionClick(sender, new RoutedEventArgs()); e.Handled = true; }
        if (e.Key == Key.Escape)
        {
            if (CorrectionPanel.IsVisible) CorrectionPanel.IsVisible = false;
            else if (ExplanationOverlay.IsVisible) BackExplanationClick(sender, new RoutedEventArgs());
            else CloseClick(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract)
        {
            var delta = e.Key is Key.OemPlus or Key.Add ? .5 : -.5;
            TranslationText.FontSize = ExplanationText.FontSize = Math.Clamp(TranslationText.FontSize + delta, 12, 34);
            TranslationText.LineHeight = ExplanationText.LineHeight = Math.Round(TranslationText.FontSize * 1.55, 2);
            RefreshText(); e.Handled = true;
        }
    }

    internal void DismissIfUnpinned()
    { if (IsVisible && !IsPinned) CloseClick(this, new RoutedEventArgs()); }

    private void CloseClick(object? sender, RoutedEventArgs e)
    {
        PinButton.IsChecked = false;
        TranslationRequests.Cancel();
        ExplanationRequests.Cancel();
        Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed record ExplanationAction(string Subject, ExplanationScope Scope);
internal sealed record QuestionAction(string Question, QuestionContextKind ContextKind);

public sealed class PopupMovedEventArgs(PixelPoint position, PopupOffset offset) : EventArgs
{
    public PixelPoint Position { get; } = position;
    public PopupOffset Offset { get; } = offset;
}
