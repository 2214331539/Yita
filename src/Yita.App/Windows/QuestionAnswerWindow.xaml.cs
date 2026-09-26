using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Yita.Interop;
using Yita.Models;
using Yita.Selection;
using Yita.Settings;
using Yita.Translation;

namespace Yita.Windows;

internal enum QuestionAnswerWindowMode
{
    Contextual,
    DeepSeekQuickChat,
}

internal partial class QuestionAnswerWindow : Window
{
    private const int MaximumQuestionCharacters = 2000;
    private const double DefaultTranscriptFontSize = 15.5;
    private readonly List<ConversationTurn> _completedTurns = [];
    private HwndSource? _hwndSource;
    private IntPtr _windowHandle;
    private Paragraph? _pendingAssistantParagraph;
    private string _pendingQuestion = string.Empty;
    private string _currentAnswer = string.Empty;
    private string _uiLanguage;
    private bool _isStreaming;
    private bool _isManualRecordSaving;
    private int _lastRecordedTurnCount;
    private string? _manualRecordErrorCode;
    private readonly QuestionAnswerWindowMode _mode;

    internal QuestionAnswerWindow(
        Guid sessionId,
        string uiLanguage,
        string englishFontFamily,
        string chineseFontFamily,
        QuestionAnswerWindowMode mode = QuestionAnswerWindowMode.Contextual)
    {
        SessionId = sessionId;
        _mode = mode;
        _uiLanguage = UiLanguageCatalog.Normalize(uiLanguage);
        InitializeComponent();
        SizePresetBar.ConfigureFontSize(12, 32, DefaultTranscriptFontSize);
        SizePresetBar.PresetSelected += SizePresetBar_PresetSelected;
        SizePresetBar.FontSizeChanged += SizePresetBar_FontSizeChanged;
        if (_mode == QuestionAnswerWindowMode.DeepSeekQuickChat)
        {
            Width = 430;
            Height = 320;
            MinWidth = 340;
            MinHeight = 220;
            SetPinned(true);
        }

        SourceInitialized += OnSourceInitialized;
        ApplyAppearance(_uiLanguage, englishFontFamily, chineseFontFamily);
        TranscriptBox.Document.PagePadding = new Thickness(0);
        TranscriptBox.Document.Blocks.Clear();
        ApplyTranscriptFontSize(SizePresetBar.FontSizeValue);
    }

    internal Guid SessionId { get; }

    internal bool IsPinned { get; private set; }

    internal bool IsStreaming => _isStreaming;

    internal bool IsDeepSeekQuickChat => _mode == QuestionAnswerWindowMode.DeepSeekQuickChat;

    internal string UiLanguage => _uiLanguage;

    internal IReadOnlyList<ConversationTurn> CompletedTurns => _completedTurns.ToArray();

    internal string PendingQuestion => _pendingQuestion;

    internal event Action<QuestionAnswerWindow, string>? QuestionSubmitted;

    internal event Action<QuestionAnswerWindow>? StopRequested;

    internal event Action<QuestionAnswerWindow>? RetryRequested;

    internal event Action<QuestionAnswerWindow>? ManualRecordRequested;

    internal event Action<QuestionAnswerWindow, bool>? PinStateChanged;

    internal void ApplyAppearance(
        string uiLanguage,
        string englishFontFamily,
        string chineseFontFamily)
    {
        _uiLanguage = UiLanguageCatalog.Normalize(uiLanguage);
        SizePresetBar.ApplyUiLanguage(_uiLanguage);
        var isChinese = _uiLanguage == UiLanguageCatalog.SimplifiedChineseLanguageId;
        TitleText.Text = IsDeepSeekQuickChat
            ? "DeepSeek"
            : isChinese ? "AI 问答" : "Ask AI";
        CopyButton.Content = isChinese ? "复制" : "Copy";
        UpdateRecordButtonState();
        StopButton.Content = isChinese ? "停止" : "Stop";
        RetryButton.Content = isChinese ? "重试" : "Retry";
        SendButton.Content = isChinese ? "提问" : "Ask";
        PinButton.ToolTip = IsDeepSeekQuickChat
            ? isChinese ? "保持置顶" : "Keep on top"
            : isChinese ? "保留此问答" : "Keep this answer";
        CloseButton.ToolTip = isChinese ? "关闭" : "Close";
        QuestionInputTextBox.ToolTip = isChinese
            ? "直接提问；Enter 发送，Shift+Enter 换行"
            : "Ask directly; Enter sends, Shift+Enter adds a line";
        QuestionInputPlaceholder.Text = IsDeepSeekQuickChat
            ? isChinese ? "问 DeepSeek…" : "Ask DeepSeek…"
            : isChinese ? "继续提问…" : "Ask a follow-up…";
        EmptyStateText.Text = IsDeepSeekQuickChat
            ? isChinese ? "输入一个简单问题，DeepSeek 会直接回答。" : "Ask a quick question and get a direct DeepSeek answer."
            : isChinese ? "可以继续追问当前内容。" : "Ask a follow-up about the current content.";
        TranscriptBox.FontFamily = CreateFontFamily(isChinese ? chineseFontFamily : englishFontFamily);
        // A theme change can change the corner radius without changing the window's size.
        UpdateSurfaceClip();
    }

    internal void BeginQuestion(string question, bool isRetry)
    {
        var normalized = TextNormalizer.Normalize(question);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!isRetry)
        {
            _pendingQuestion = normalized;
            _manualRecordErrorCode = null;
            EmptyStateText.Visibility = Visibility.Collapsed;
            AppendMessage(
                _uiLanguage == UiLanguageCatalog.SimplifiedChineseLanguageId ? "你" : "You",
                normalized,
                isUser: true);
        }
        else if (_pendingAssistantParagraph is not null)
        {
            TranscriptBox.Document.Blocks.Remove(_pendingAssistantParagraph);
        }

        _pendingAssistantParagraph = new Paragraph
        {
            Margin = new Thickness(0, 5, 0, 12),
            LineHeight = GetTranscriptLineHeight(SizePresetBar.FontSizeValue),
        };
        TranscriptBox.Document.Blocks.Add(_pendingAssistantParagraph);
        _currentAnswer = string.Empty;
        _isStreaming = true;
        SetPendingAssistantContent(HighlightedText.FromPlainText(
            _uiLanguage == UiLanguageCatalog.SimplifiedChineseLanguageId
                ? "正在思考…"
                : "Thinking…"));
        StopButton.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Collapsed;
        SendButton.IsEnabled = false;
        QuestionInputTextBox.IsEnabled = false;
        UpdateRecordButtonState();
        TranscriptBox.ScrollToEnd();
    }

    internal void UpdateAnswer(string answer)
    {
        if (!_isStreaming || _pendingAssistantParagraph is null)
        {
            return;
        }

        var highlightedAnswer = HighlightMarkup.EnsureEmphasis(HighlightMarkup.Parse(answer));
        _currentAnswer = highlightedAnswer.PlainText;
        SetPendingAssistantContent(highlightedAnswer);
        TranscriptBox.ScrollToEnd();
    }

    internal void CompleteAnswer(string answer)
    {
        if (!_isStreaming || string.IsNullOrWhiteSpace(_pendingQuestion))
        {
            return;
        }

        UpdateAnswer(answer);
        _completedTurns.Add(new ConversationTurn("user", _pendingQuestion));
        _completedTurns.Add(new ConversationTurn("assistant", _currentAnswer));
        _pendingQuestion = string.Empty;
        _pendingAssistantParagraph = null;
        _currentAnswer = string.Empty;
        SetIdleState();
    }

    internal void FailAnswer(string message)
    {
        SetPendingAssistantContent(HighlightedText.FromPlainText(message));

        _isStreaming = false;
        StopButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = string.IsNullOrWhiteSpace(_pendingQuestion)
            ? Visibility.Collapsed
            : Visibility.Visible;
        SendButton.IsEnabled = true;
        QuestionInputTextBox.IsEnabled = true;
        UpdateRecordButtonState();
    }

    internal void MarkStopped()
    {
        if (!_isStreaming)
        {
            return;
        }

        var suffix = _uiLanguage == UiLanguageCatalog.SimplifiedChineseLanguageId
            ? "\n\n（已停止）"
            : "\n\n(Stopped)";
        SetPendingAssistantContent(HighlightedText.FromPlainText((_currentAnswer + suffix).Trim()));

        _isStreaming = false;
        StopButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Visible;
        SendButton.IsEnabled = true;
        QuestionInputTextBox.IsEnabled = true;
        UpdateRecordButtonState();
    }

    internal void ShowNear(Window parent)
    {
        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        var parentHandle = new WindowInteropHelper(parent).Handle;
        var screen = System.Windows.Forms.Screen.FromHandle(parentHandle);
        var dpi = VisualTreeHelper.GetDpi(this);
        var workArea = screen.WorkingArea;
        var leftBoundary = workArea.Left / dpi.DpiScaleX;
        var rightBoundary = workArea.Right / dpi.DpiScaleX;
        var topBoundary = workArea.Top / dpi.DpiScaleY;
        var bottomBoundary = workArea.Bottom / dpi.DpiScaleY;
        var preferredLeft = parent.Left + parent.ActualWidth + 12;
        if (preferredLeft + ActualWidth > rightBoundary)
        {
            preferredLeft = parent.Left - ActualWidth - 12;
        }

        Left = Math.Clamp(preferredLeft, leftBoundary, Math.Max(leftBoundary, rightBoundary - ActualWidth));
        Top = Math.Clamp(parent.Top, topBoundary, Math.Max(topBoundary, bottomBoundary - ActualHeight));
    }

    internal void FocusQuestionInput()
    {
        if (!IsVisible)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () =>
            {
                Activate();
                QuestionInputTextBox.Focus();
                Keyboard.Focus(QuestionInputTextBox);
            },
            DispatcherPriority.Input);
    }

    internal void SetPinned(bool isPinned)
    {
        if (IsPinned == isPinned)
        {
            PinButton.Tag = isPinned ? "Pinned" : null;
            return;
        }

        IsPinned = isPinned;
        PinButton.Tag = IsPinned ? "Pinned" : null;
        PinStateChanged?.Invoke(this, IsPinned);
    }

    internal void ApplySizePresetForVisualTest(WindowSizePreset preset) => ApplySizePreset(preset);

    internal bool HasDirectFontSizeSliderForVisualTest() =>
        SizePresetBar.HasDirectFontSizeSliderForVisualTest();

    internal bool HasSingleRowExternalControlsForVisualTest() =>
        SizePresetBar.HasSingleRowLayoutForVisualTest();

    internal bool HasMatchingSurfaceClipForVisualTest() =>
        Surface.Clip is RectangleGeometry clip
        && Math.Abs(clip.RadiusX - Surface.CornerRadius.TopLeft) < 0.1
        && Math.Abs(clip.Rect.Width - Surface.ActualWidth) < 1
        && Math.Abs(clip.Rect.Height - Surface.ActualHeight) < 1;

    internal bool HasUsableGlassRimForVisualTest() =>
        GlassSurfaceRim.IsGlassEnabled
        && !GlassSurfaceRim.IsHitTestVisible
        && Math.Abs(GlassSurfaceRim.ActualWidth - Surface.ActualWidth) < 1
        && Math.Abs(GlassSurfaceRim.ActualHeight - Surface.ActualHeight) < 1
        && HasMatchingSurfaceClipForVisualTest()
        && Opacity == 1;

    internal bool HasReadableGlassTextSurfaceForVisualTest() =>
        TranscriptTextSurface.Background is LinearGradientBrush brush
        && brush.GradientStops.All(stop => stop.Color.A == 255
            && stop.Color != System.Windows.Media.Colors.White)
        && brush.Opacity == 1
        && TranscriptTextSurface.Opacity == 1
        && TranscriptTextSurface.CornerRadius == new CornerRadius(18)
        && TranscriptTextSurface.Padding == new Thickness(12)
        && TranscriptTextSurface.ActualWidth >= TranscriptBox.ActualWidth + 23
        && TranscriptTextSurface.ActualHeight >= TranscriptBox.ActualHeight + 23
        && TranscriptBox.IsHitTestVisible
        && TranscriptBox.Effect is null;

    internal void SetFontSizeForVisualTest(double value) => SizePresetBar.FontSizeValue = value;

    internal double TranscriptFontSizeForVisualTest => TranscriptBox.FontSize;

    internal bool HasRecordButtonForVisualTest() => RecordButton.Visibility == Visibility.Visible;

    internal bool IsRecordEnabledForVisualTest() => RecordButton.IsEnabled;

    internal bool HasContentHighlightsForVisualTest() =>
        TranscriptBox.Document.Blocks
            .OfType<Paragraph>()
            .Any(HighlightedTextRenderer.HasHighlightRuns);

    internal bool ContainsScreenPoint(ScreenPoint point)
    {
        return _windowHandle != IntPtr.Zero
               && NativeMethods.GetWindowRect(_windowHandle, out var bounds)
               && bounds.Contains(point);
    }

    private void SubmitQuestion()
    {
        if (_isStreaming)
        {
            return;
        }

        var question = TextNormalizer.Normalize(QuestionInputTextBox.Text);
        if (string.IsNullOrWhiteSpace(question))
        {
            return;
        }

        if (question.Length > MaximumQuestionCharacters)
        {
            question = question[..MaximumQuestionCharacters];
        }

        QuestionInputTextBox.Clear();
        QuestionSubmitted?.Invoke(this, question);
    }

    private void AppendMessage(string role, string content, bool isUser)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, isUser ? 2 : 5, 0, 8),
            LineHeight = GetTranscriptLineHeight(SizePresetBar.FontSizeValue),
        };
        paragraph.Inlines.Add(new Bold(new Run(role)));
        paragraph.Inlines.Add(new LineBreak());
        paragraph.Inlines.Add(new Run(content));
        TranscriptBox.Document.Blocks.Add(paragraph);
    }

    private void SetPendingAssistantContent(HighlightedText text)
    {
        if (_pendingAssistantParagraph is null)
        {
            return;
        }

        _pendingAssistantParagraph.Inlines.Clear();
        _pendingAssistantParagraph.Inlines.Add(new Bold(new Run("AI")));
        _pendingAssistantParagraph.Inlines.Add(new LineBreak());
        HighlightedTextRenderer.AppendUniformRuns(
            _pendingAssistantParagraph,
            text,
            TranscriptBox.FontFamily);
    }

    private void SetIdleState()
    {
        _isStreaming = false;
        StopButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        SendButton.IsEnabled = true;
        QuestionInputTextBox.IsEnabled = true;
        UpdateRecordButtonState();
        QuestionInputTextBox.Focus();
        TranscriptBox.ScrollToEnd();
    }

    private void QuestionInputTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SubmitQuestion();
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        e.Handled = true;
        ChangeTranscriptFontSize(e.Delta > 0 ? 1 : -1);
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        if (e.Key is Key.Add or Key.OemPlus)
        {
            e.Handled = true;
            ChangeTranscriptFontSize(1);
        }
        else if (e.Key is Key.Subtract or Key.OemMinus)
        {
            e.Handled = true;
            ChangeTranscriptFontSize(-1);
        }
        else if (e.Key is Key.D0 or Key.NumPad0)
        {
            e.Handled = true;
            SizePresetBar.FontSizeValue = DefaultTranscriptFontSize;
        }
    }

    private void ChangeTranscriptFontSize(double delta)
    {
        SizePresetBar.FontSizeValue = Math.Clamp(
            SizePresetBar.FontSizeValue + delta,
            SizePresetBar.MinimumFontSize,
            SizePresetBar.MaximumFontSize);
    }

    private void SizePresetBar_FontSizeChanged(object? sender, WindowFontSizeChangedEventArgs e)
    {
        ApplyTranscriptFontSize(e.NewValue);
    }

    private void ApplyTranscriptFontSize(double fontSize)
    {
        if (!double.IsFinite(fontSize) || fontSize <= 0 || TranscriptBox is null)
        {
            return;
        }

        TranscriptBox.FontSize = fontSize;
        TranscriptBox.Document.FontSize = fontSize;
        var lineHeight = GetTranscriptLineHeight(fontSize);
        foreach (var paragraph in TranscriptBox.Document.Blocks.OfType<Paragraph>())
        {
            paragraph.FontSize = fontSize;
            paragraph.LineHeight = lineHeight;
        }
    }

    private static double GetTranscriptLineHeight(double fontSize) =>
        Math.Round(Math.Max(18, fontSize * 1.48), 1);

    private void SendButton_Click(object sender, RoutedEventArgs e) => SubmitQuestion();

    private void QuestionInputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (QuestionInputPlaceholder is not null)
        {
            QuestionInputPlaceholder.Visibility = string.IsNullOrEmpty(QuestionInputTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopRequested?.Invoke(this);
        MarkStopped();
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_pendingQuestion))
        {
            RetryRequested?.Invoke(this);
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var text = new TextRange(TranscriptBox.Document.ContentStart, TranscriptBox.Document.ContentEnd)
            .Text
            .Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another process can briefly own the clipboard. Keep the Q&A
                // window responsive and let the user retry without losing text.
            }
        }
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isStreaming
            || _isManualRecordSaving
            || _completedTurns.Count == 0
            || _completedTurns.Count <= _lastRecordedTurnCount)
        {
            return;
        }

        if (ManualRecordRequested is { } requested)
        {
            requested(this);
            return;
        }

        MarkManualRecordFailed("unavailable");
    }

    internal int BeginManualRecordSave()
    {
        if (_isStreaming
            || _completedTurns.Count == 0
            || _completedTurns.Count <= _lastRecordedTurnCount)
        {
            return 0;
        }

        _isManualRecordSaving = true;
        _manualRecordErrorCode = null;
        UpdateRecordButtonState();
        return _completedTurns.Count;
    }

    internal void MarkManualRecordSaved(int recordedTurnCount)
    {
        _isManualRecordSaving = false;
        _lastRecordedTurnCount = Math.Max(
            _lastRecordedTurnCount,
            Math.Min(recordedTurnCount, _completedTurns.Count));
        _manualRecordErrorCode = null;
        UpdateRecordButtonState();
    }

    internal void MarkManualRecordFailed(string? errorCode)
    {
        _isManualRecordSaving = false;
        _manualRecordErrorCode = errorCode;
        UpdateRecordButtonState();
    }

    private void UpdateRecordButtonState()
    {
        if (RecordButton is null)
        {
            return;
        }

        var isChinese = _uiLanguage == UiLanguageCatalog.SimplifiedChineseLanguageId;
        if (_isManualRecordSaving)
        {
            RecordButton.Content = isChinese ? "保存中…" : "Saving…";
            RecordButton.ToolTip = isChinese
                ? "正在保存到今天的 Markdown 记录"
                : "Saving to today's Markdown record";
        }
        else if (_completedTurns.Count > 0 && _completedTurns.Count <= _lastRecordedTurnCount)
        {
            RecordButton.Content = isChinese ? "已保存" : "Saved";
            RecordButton.ToolTip = isChinese
                ? "当前完整对话已记录"
                : "The current conversation is recorded";
        }
        else if (_manualRecordErrorCode is not null)
        {
            RecordButton.Content = isChinese ? "重试" : "Retry";
            RecordButton.ToolTip = _manualRecordErrorCode switch
            {
                "directory_required" => isChinese
                    ? "请先在设置中选择保存目录，然后重试"
                    : "Choose a save folder in Settings, then retry",
                "schema_conflict" => isChinese
                    ? "今天的文件不是 Yita 记录，请移动或重命名后重试"
                    : "Today's file is not an Yita record; move or rename it, then retry",
                _ => isChinese
                    ? "记录保存失败，请检查目录后重试"
                    : "Could not save the record. Check the folder and retry",
            };
        }
        else
        {
            RecordButton.Content = isChinese ? "记录" : "Record";
            RecordButton.ToolTip = isChinese
                ? "将当前完整对话保存到今天的 Markdown 记录"
                : "Save the complete conversation to today's Markdown record";
        }

        RecordButton.IsEnabled = !_isStreaming
                                 && !_isManualRecordSaving
                                 && _completedTurns.Count > _lastRecordedTurnCount;
        System.Windows.Automation.AutomationProperties.SetName(
            RecordButton,
            isChinese ? "记录当前对话" : "Record current conversation");
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        SetPinned(!IsPinned);
    }

    private void SizePresetBar_PresetSelected(
        object? sender,
        WindowSizePresetSelectedEventArgs e)
    {
        ApplySizePreset(e.Preset);
    }

    private void ApplySizePreset(WindowSizePreset preset)
    {
        WindowSizePresetCatalog.Apply(this, WindowSizePresetCatalog.ForConversation(preset));
        FocusQuestionInput();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || FindAncestor<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSurfaceClip();
    }

    private void UpdateSurfaceClip()
    {
        if (Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0)
        {
            return;
        }

        var corner = System.Windows.Application.Current.Resources["PopupSurfaceCornerRadius"] is CornerRadius radius
            ? radius.TopLeft
            : 18;
        var clip = new RectangleGeometry(
            new Rect(0, 0, Surface.ActualWidth, Surface.ActualHeight),
            corner,
            corner);
        clip.Freeze();
        Surface.Clip = clip;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_windowHandle);
        _hwndSource?.AddHook(WindowProcedure);
        var styles = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(
            _windowHandle,
            NativeMethods.GwlExStyle,
            new IntPtr((styles | NativeMethods.WsExToolWindow) & ~NativeMethods.WsExNoActivate));
    }

    private IntPtr WindowProcedure(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == NativeMethods.WmNcHitTest
            && IsVisible
            && NativeMethods.GetWindowRect(windowHandle, out var bounds))
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var edge = Math.Max(9, (int)Math.Ceiling(9 * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)));
            var hit = PopupResizeHitTest.Resolve(
                bounds,
                PopupResizeHitTest.DecodeScreenPoint(lParam),
                edge);
            if (hit != 0)
            {
                handled = true;
                return new IntPtr(hit);
            }
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WindowProcedure);
            _hwndSource = null;
        }

        SizePresetBar.PresetSelected -= SizePresetBar_PresetSelected;
        SizePresetBar.FontSizeChanged -= SizePresetBar_FontSizeChanged;

        TranscriptBox.Document.Blocks.Clear();
        _completedTurns.Clear();
        _pendingAssistantParagraph = null;
        _pendingQuestion = string.Empty;
        _currentAnswer = string.Empty;
        _windowHandle = IntPtr.Zero;
        base.OnClosed(e);
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
            {
                return match;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private static System.Windows.Media.FontFamily CreateFontFamily(string familyName)
    {
        if (familyName.Equals("Source Sans Pro", StringComparison.OrdinalIgnoreCase))
        {
            return new System.Windows.Media.FontFamily("pack://application:,,,/Assets/Fonts/#Source Sans Pro");
        }

        return new System.Windows.Media.FontFamily(familyName);
    }
}
