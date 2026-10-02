using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Yita.Core;
using Yita.Core.Parity;
using Yita.Services;
using Yita.Settings;
using Yita.Translation;

namespace Yita.Desktop;

internal sealed partial class QuestionAnswerWindow : Window
{
    private AppSettings _settings;
    private readonly string _source;
    private readonly string _translation;
    private readonly string _explanation;
    private readonly QuestionContextKind _kind;
    private readonly ReferenceTranslationRuntime _runtime;
    private readonly AiHistoryStore _records;
    private readonly AiHistoryContext _context;
    private readonly List<ConversationTurn> _turns = [];
    private readonly LatestRequestController _requests = new();
    private string _question = "";
    private bool _closed;
    private bool _streaming;
    private bool _waitingForAnswer;
    private string _transcript = "";
    private bool _savingRecord;
    private int _recordedTurnCount;
    private string? _recordError;
    internal bool IsPinned => PinButton.IsChecked == true;

    public QuestionAnswerWindow(AppSettings settings, string source, string translation, string explanation,
        QuestionContextKind kind, ReferenceTranslationRuntime runtime, AiHistoryStore records, AiHistoryContext context)
    {
        (_settings, _source, _translation, _explanation, _kind, _runtime, _records, _context) =
            (settings, source, translation, explanation, kind, runtime, records, context);
        InitializeComponent();
        DesktopFloatingWindowBehavior.Apply(this);
        ReferenceTheme.Apply(Resources, settings);
        ApplyUiLanguage(settings.UiLanguage);
        var chat = kind == QuestionContextKind.GeneralChat;
        SizePresetBar.TextSizeChanged += (_, size) => { TranscriptText.FontSize = size; RenderTranscript(); };
        SizePresetBar.ConfigureTextSize(12, 32, 15.5);
        SizePresetBar.PresetSelected += (_, preset) => WindowSizePresetBar.ApplyConversationPreset(this, preset);
        if (chat) { Width = 430; Height = 320; MinWidth = 340; MinHeight = 220; PinButton.IsChecked = true; }
        SizeChanged += (_, _) => UpdateSurfaceClip();
        Opened += (_, _) => { WindowSizePresetBar.Constrain(this); ReferenceMotion.Reveal(Surface); };
        Closed += (_, _) => { _closed = true; _requests.Dispose(); };
    }

    internal void ApplyUiLanguage(string language)
    {
        _settings = _settings with { UiLanguage = UiLanguageCatalog.Normalize(language) };
        var chinese = _settings.UiLanguage == "zh-CN";
        TitleText.Text = _kind == QuestionContextKind.GeneralChat ? "DeepSeek" : chinese ? "AI 问答" : "Ask AI";
        CopyButton.Content = chinese ? "复制" : "Copy";
        RecordButton.Content = chinese ? "记录" : "Record";
        StopButton.Content = chinese ? "停止" : "Stop";
        RetryButton.Content = chinese ? "重试" : "Retry";
        SendButton.Content = chinese ? "提问" : "Ask";
        var chat = _kind == QuestionContextKind.GeneralChat;
        EmptyStateText.Text = chat
            ? chinese ? "输入一个简单问题，DeepSeek 会直接回答。" : "Ask a quick question and get a direct DeepSeek answer."
            : chinese ? "可以继续追问当前内容。" : "Ask a follow-up about the current content.";
        QuestionInputTextBox.Watermark = chat
            ? chinese ? "问 DeepSeek…" : "Ask DeepSeek…"
            : chinese ? "继续提问…" : "Ask a follow-up…";
        ToolTip.SetTip(PinButton, chinese ? "固定窗口" : "Keep window");
        ToolTip.SetTip(CloseButton, chinese ? "关闭" : "Close");
        TranscriptText.FontFamily = ReferenceTypography.CreateFont(chinese ? _settings.ChineseTranslationFontFamily : _settings.EnglishTranslationFontFamily);
        SizePresetBar.ApplyUiLanguage(_settings.UiLanguage);
        if (_waitingForAnswer) _pendingAnswer = chinese ? "正在回答…" : "Answering…";
        UpdateRecordState();
        RenderTranscript();
    }

    internal async Task AskAsync(string question)
    {
        question = question.Trim();
        if (_closed || _streaming || question.Length == 0) return;
        if (question.Length > 2000) question = question[..2000];
        using var pending = _requests.Begin();
        _question = question;
        _streaming = true;
        _waitingForAnswer = true;
        _recordError = null;
        StopButton.IsVisible = true;
        RetryButton.IsVisible = false;
        SendButton.IsEnabled = false;
        QuestionInputTextBox.IsEnabled = false;
        UpdateRecordState();
        EmptyStateText.IsVisible = false;
        QuestionInputTextBox.Text = "";
        var request = new QuestionAnswerRequest(question, _source, _translation, _explanation,
            _context.SourceLanguage, _context.TargetLanguage, _settings.UiLanguage, _kind, _turns.ToArray());
        var answer = "";
        RenderPending(question, _settings.UiLanguage == "zh-CN" ? "正在回答…" : "Answering…");
        try
        {
            await foreach (var output in _runtime.AnswerAsync(request, _settings, pending.Token))
            {
                if (!pending.IsCurrent) return;
                _waitingForAnswer = false;
                answer = output;
                RenderPending(question, output);
            }
            if (!pending.IsCurrent) return;
            if (answer.Length == 0) throw new InvalidOperationException(_settings.UiLanguage == "zh-CN" ? "未返回回答。" : "No answer returned.");
            _turns.Add(new ConversationTurn("user", question));
            _turns.Add(new ConversationTurn("assistant", answer));
            _pendingAnswer = null;
            RenderTranscript();
            var saved = await _records.AppendQuestionAnswerAsync(_settings, _context with { TranslationText = _translation },
                question, answer, _kind, DateTimeOffset.Now, pending.Token);
            if (pending.IsCurrent && saved.ErrorCode is not null) TitleText.Text = _settings.UiLanguage == "zh-CN" ? "记录保存失败" : "Record save failed";
        }
        catch (OperationCanceledException)
        {
            if (pending.IsCurrent) RenderPending(question, answer.Length == 0 ? (_settings.UiLanguage == "zh-CN" ? "已停止。" : "Stopped.") : answer);
        }
        catch (Exception exception)
        {
            if (pending.IsCurrent) { RenderPending(question, exception.Message); RetryButton.IsVisible = true; }
        }
        finally
        {
            if (pending.IsCurrent) { SetIdle(); }
        }
    }

    private void RenderPending(string question, string answer)
    {
        _pendingAnswer = answer;
        RenderTranscript();
        TranscriptScroll.ScrollToEnd();
    }

    private string? _pendingAnswer;
    private void RenderTranscript()
    {
        var messages = _turns.ToList();
        if (_pendingAnswer is not null)
        {
            messages.Add(new ConversationTurn("user", _question));
            messages.Add(new ConversationTurn("assistant", _pendingAnswer));
        }
        _transcript = string.Join("\n\n", messages.Select(turn => Role(turn) + "\n" + turn.Content));
        TranscriptText.Text = null;
        TranscriptText.Inlines!.Clear();
        foreach (var turn in messages)
        {
            if (TranscriptText.Inlines.Count > 0)
            {
                TranscriptText.Inlines.Add(new Run("\n") { FontSize = TranscriptText.FontSize });
                // WPF paragraphs leave a compact gap, rather than a full blank text line.
                TranscriptText.Inlines.Add(new Run("\n") { FontSize = 5.5 });
            }
            TranscriptText.Inlines.Add(new Run(Role(turn)) { FontWeight = FontWeight.Bold, FontSize = TranscriptText.FontSize });
            TranscriptText.Inlines.Add(new Run("\n" + turn.Content) { FontSize = TranscriptText.FontSize });
        }
        string Role(ConversationTurn turn) => turn.Role == "user" ? (_settings.UiLanguage == "zh-CN" ? "你" : "You") : "AI";
    }

    private async void SendClick(object? sender, RoutedEventArgs e) => await AskAsync(QuestionInputTextBox.Text ?? "");
    private void StopClick(object? sender, RoutedEventArgs e)
    {
        _requests.Cancel(); SetIdle();
        RetryButton.IsVisible = true;
    }

    internal void SuspendSession()
    {
        if (!_streaming) return;
        _requests.Cancel();
        if (_waitingForAnswer) _pendingAnswer = _settings.UiLanguage == "zh-CN" ? "已停止。" : "Stopped.";
        SetIdle();
        RenderTranscript();
        RetryButton.IsVisible = true;
    }
    private async void RetryClick(object? sender, RoutedEventArgs e) => await AskAsync(_question);
    private async void CopyClick(object? sender, RoutedEventArgs e)
    {
        try { if (Clipboard is { } clipboard) await clipboard.SetTextAsync(_transcript); }
        catch { TitleText.Text = _settings.UiLanguage == "zh-CN" ? "复制失败" : "Copy failed"; }
    }
    private async void RecordClick(object? sender, RoutedEventArgs e)
    {
        if (_closed || _streaming || _savingRecord || _turns.Count <= _recordedTurnCount) return;
        var turns = _turns.ToArray();
        _savingRecord = true;
        _recordError = null;
        UpdateRecordState();
        try
        {
            var result = await _records.AppendManualRecordAsync(_settings, new ManualConversationRecordRequest(DateTimeOffset.Now,
                _context.SessionId, _settings.UiLanguage, _source, _translation, _context.SourceLanguage, _context.TargetLanguage,
                _explanation, _kind, turns));
            if (result.Saved) _recordedTurnCount = Math.Max(_recordedTurnCount, turns.Length);
            else _recordError = result.ErrorCode;
        }
        catch { _recordError = "write_failed"; }
        finally { _savingRecord = false; if (!_closed) UpdateRecordState(); }
    }
    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
    private void HeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual visual && visual.GetVisualAncestors().Prepend(visual).Any(item => item is Button || item is ToggleButton)) return;
        BeginMoveDrag(e);
    }
    private void ResizePointerPressed(object? sender, PointerPressedEventArgs e)
    { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginResizeDrag(WindowEdge.SouthEast, e); }
    private void InputKeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { SendClick(sender, new RoutedEventArgs()); e.Handled = true; } }
    private void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !IsPinned) { Close(); e.Handled = true; }
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract or Key.D0 or Key.NumPad0)
        {
            SizePresetBar.TextSize = e.Key is Key.D0 or Key.NumPad0 ? 15.5
                : SizePresetBar.TextSize + (e.Key is Key.OemPlus or Key.Add ? 1 : -1);
            e.Handled = true;
        }
    }
    private void WindowPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        SizePresetBar.TextSize += e.Delta.Y > 0 ? 1 : -1;
        e.Handled = true;
    }
    private void SetIdle()
    {
        _streaming = false;
        _waitingForAnswer = false;
        StopButton.IsVisible = false;
        SendButton.IsEnabled = true;
        QuestionInputTextBox.IsEnabled = true;
        UpdateRecordState();
    }
    private void UpdateRecordState()
    {
        var chinese = _settings.UiLanguage == "zh-CN";
        var saved = _turns.Count > 0 && _turns.Count <= _recordedTurnCount;
        RecordButton.Content = _savingRecord ? (chinese ? "保存中…" : "Saving…")
            : saved ? (chinese ? "已保存" : "Saved")
            : _recordError is not null ? (chinese ? "重试" : "Retry") : (chinese ? "记录" : "Record");
        ToolTip.SetTip(RecordButton, _recordError == "directory_required"
            ? (chinese ? "请先在设置中选择保存目录，然后重试" : "Choose a save folder in Settings, then retry")
            : (chinese ? "将当前完整对话保存到今天的 Markdown 记录" : "Save the complete conversation to today's Markdown record"));
        RecordButton.IsEnabled = !_streaming && !_savingRecord && _turns.Count > _recordedTurnCount;
    }
    private void UpdateSurfaceClip()
    {
        if (Surface.Bounds.Width <= 0 || Surface.Bounds.Height <= 0) return;
        Surface.Clip = new RectangleGeometry(new Rect(Surface.Bounds.Size), Surface.CornerRadius.TopLeft, Surface.CornerRadius.TopLeft);
    }
}
