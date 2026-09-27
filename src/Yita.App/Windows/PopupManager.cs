using Yita.Models;
using Yita.Services;
using Yita.Settings;
using Yita.Translation;

namespace Yita.Windows;

internal sealed class PopupManager : IPopupPresenter, IDisposable
{
    private readonly Dictionary<long, PopupWindow> _windows = new();
    private readonly Dictionary<Guid, QuestionAnswerWindow> _questionWindows = new();
    private readonly Dictionary<Guid, QuestionContextSnapshot> _questionContexts = new();
    private readonly Guid _deepSeekChatSessionId = Guid.NewGuid();
    private readonly Func<PopupAppearanceSettings> _getAppearanceSettings;
    private readonly Func<ManualAiRecordRequest, CancellationToken, Task<AiHistoryWriteResult>>? _manualRecordWriter;
    private long? _transientRequestId;
    private bool _disposing;
    private readonly PopupPlacementStore _placementStore = new();

    public PopupManager(
        Func<double>? getDefaultFontSize = null,
        Func<ManualAiRecordRequest, CancellationToken, Task<AiHistoryWriteResult>>? manualRecordWriter = null)
    {
        _getAppearanceSettings = getDefaultFontSize is null
            ? () => PopupAppearanceSettings.Default
            : () => PopupAppearanceSettings.Default with { FontSize = getDefaultFontSize() };
        _manualRecordWriter = manualRecordWriter;
    }

    public PopupManager(
        Func<PopupAppearanceSettings> getAppearanceSettings,
        Func<ManualAiRecordRequest, CancellationToken, Task<AiHistoryWriteResult>>? manualRecordWriter = null)
    {
        _getAppearanceSettings = getAppearanceSettings
            ?? throw new ArgumentNullException(nameof(getAppearanceSettings));
        _manualRecordWriter = manualRecordWriter;
    }

    public event Action<PopupRetranslateRequest>? RetranslateRequested;

    public event Action<long>? PopupClosed;

    public event Action<long, bool>? PinStateChanged;

    public event Func<PopupTranslationCorrection, bool>? TranslationCorrectionRequested;

    public event Action<PopupExplanationRequest>? ExplanationRequested;

    public event Action<long>? ExplanationDismissed;

    public event Action<PopupQuestionAnswerRequest>? QuestionAnswerRequested;

    public event Action<Guid>? QuestionAnswerCancelled;

    public string CreateStatusReport(bool useChinese)
    {
        var pinnedCount = _windows.Values.Count(window => window.IsPinned);
        return useChinese
            ? $"浮窗状态{Environment.NewLine}总数：{_windows.Count}{Environment.NewLine}保留：{pinnedCount}"
            : $"Popup status{Environment.NewLine}Total: {_windows.Count}{Environment.NewLine}Pinned: {pinnedCount}";
    }

    public void ShowLoading(long requestId, ScreenPoint anchorPoint)
    {
        var window = GetOrCreateWindow(requestId);
        if (!window.IsPinned)
        {
            CloseOtherTransient(requestId);
            _transientRequestId = requestId;
        }

        window.ShowLoading(anchorPoint);
    }

    public void ShowTranslation(
        long requestId,
        string sourceText,
        string translatedText,
        string targetLanguage,
        ScreenPoint anchorPoint,
        string sourceLanguage = "自动检测")
    {
        var window = GetOrCreateWindow(requestId);
        if (!window.IsPinned)
        {
            CloseOtherTransient(requestId);
            _transientRequestId = requestId;
        }

        window.ShowTranslation(
            sourceText,
            translatedText,
            targetLanguage,
            anchorPoint,
            sourceLanguage);
    }

    public void FailRequest(long requestId, string? message = null)
    {
        if (!_windows.TryGetValue(requestId, out var window))
        {
            return;
        }

        if (window.HasTranslation)
        {
            window.RestoreTranslationAfterFailure();
        }
        else
        {
            window.ShowFailure(message);
        }
    }

    public void CompleteRequest(long requestId)
    {
        if (_windows.TryGetValue(requestId, out var window))
        {
            window.MarkTranslationComplete();
        }
    }

    public void ShowExplanationLoading(long requestId)
    {
        if (_windows.TryGetValue(requestId, out var window))
        {
            window.ShowExplanationLoading();
        }
    }

    public void ShowExplanation(long requestId, string explanation)
    {
        if (_windows.TryGetValue(requestId, out var window))
        {
            window.ShowExplanation(explanation);
        }
    }

    public void CompleteExplanation(long requestId)
    {
        if (_windows.TryGetValue(requestId, out var window))
        {
            window.MarkExplanationComplete();
        }
    }

    public void FailExplanation(long requestId, string message)
    {
        if (_windows.TryGetValue(requestId, out var window))
        {
            window.ShowExplanationFailure(message);
        }
    }

    public void HideExplanation(long requestId)
    {
        if (_windows.TryGetValue(requestId, out var window))
        {
            window.HideExplanation(notifyDismissed: false);
        }
    }

    public void ShowQuestionAnswer(Guid sessionId, string answer)
    {
        if (_questionWindows.TryGetValue(sessionId, out var window))
        {
            window.UpdateAnswer(answer);
        }
    }

    public void CompleteQuestionAnswer(Guid sessionId, string answer)
    {
        if (_questionWindows.TryGetValue(sessionId, out var window))
        {
            window.CompleteAnswer(answer);
        }
    }

    public void FailQuestionAnswer(Guid sessionId, string message)
    {
        if (_questionWindows.TryGetValue(sessionId, out var window))
        {
            window.FailAnswer(message);
        }
    }

    public bool IsPointOverPopup(ScreenPoint point)
    {
        return _windows.Values.Any(window => window.ContainsScreenPoint(point))
               || _questionWindows.Values.Any(window => window.ContainsScreenPoint(point));
    }

    public void HideTransientPopup()
    {
        foreach (var questionWindow in _questionWindows.Values.Where(window => !window.IsPinned).ToArray())
        {
            questionWindow.Close();
        }

        if (_transientRequestId is not { } requestId
            || !_windows.TryGetValue(requestId, out var window)
            || window.IsPinned)
        {
            return;
        }

        window.Close();
    }

    public void HideTransientPopupIfOutside(ScreenPoint point)
    {
        if (IsPointOverPopup(point))
        {
            return;
        }

        if (_transientRequestId is { } requestId
            && _windows.TryGetValue(requestId, out var window)
            && !window.IsPinned)
        {
            window.Close();
        }

        foreach (var questionWindow in _questionWindows.Values
                     .Where(candidate => !candidate.IsPinned)
                     .ToArray())
        {
            questionWindow.Close();
        }
    }

    internal PopupWindow? GetWindowForVisualTest(long requestId)
    {
        return _windows.GetValueOrDefault(requestId);
    }

    internal QuestionAnswerWindow? GetQuestionAnswerWindowForVisualTest(Guid sessionId) =>
        _questionWindows.GetValueOrDefault(sessionId);

    internal QuestionAnswerWindow? GetDeepSeekChatWindowForVisualTest() =>
        _questionWindows.GetValueOrDefault(_deepSeekChatSessionId);

    internal void OpenDeepSeekChatForVisualTest(long requestId)
    {
        if (_windows.TryGetValue(requestId, out var parent))
        {
            OnDeepSeekChatRequested(parent);
        }
    }

    public void ApplyAppearanceToOpenWindows()
    {
        var appearance = _getAppearanceSettings();
        foreach (var window in _windows.Values)
        {
            window.ApplyAppearance(
                appearance.EnglishFontFamily,
                appearance.ChineseFontFamily,
                appearance.UiLanguage,
                appearance.PopupVisualStyle);
        }


        foreach (var window in _questionWindows.Values)
        {
            window.ApplyAppearance(
                appearance.UiLanguage,
                appearance.EnglishFontFamily,
                appearance.ChineseFontFamily);
        }
    }

    public void Dispose()
    {
        if (_disposing)
        {
            return;
        }

        _disposing = true;
        foreach (var window in _windows.Values.ToArray())
        {
            window.Close();
        }

        foreach (var window in _questionWindows.Values.ToArray())
        {
            window.Close();
        }

        _windows.Clear();
        _questionWindows.Clear();
        _questionContexts.Clear();
        _transientRequestId = null;
    }

    private PopupWindow GetOrCreateWindow(long requestId)
    {
        if (_windows.TryGetValue(requestId, out var existing))
        {
            return existing;
        }

        var appearance = _getAppearanceSettings();
        var window = new PopupWindow(
            requestId,
            appearance.FontSize,
            appearance.EnglishFontFamily,
            appearance.ChineseFontFamily,
            appearance.UiLanguage,
            appearance.PopupVisualStyle);
        window.PreferredPlacement = _placementStore.Load();
        window.PlacementChanged += _placementStore.Save;
        window.RenderingFailed += OnRenderingFailed;
        window.PinStateChanged += OnPinStateChanged;
        window.RetranslateRequested += OnRetranslateRequested;
        window.CorrectionSaveRequested += OnCorrectionSaveRequested;
        window.ExplanationRequested += OnExplanationRequested;
        window.ExplanationDismissed += OnExplanationDismissed;
        window.ManualExplanationRecordRequested += OnManualExplanationRecordRequested;
        window.QuestionSubmitted += OnInlineQuestionSubmitted;
        window.DeepSeekChatRequested += OnDeepSeekChatRequested;
        window.Closed += OnWindowClosed;
        _windows.Add(requestId, window);
        return window;
    }

    private void CloseOtherTransient(long requestId)
    {
        if (_transientRequestId is not { } existingId || existingId == requestId)
        {
            return;
        }

        if (_windows.TryGetValue(existingId, out var existingWindow) && !existingWindow.IsPinned)
        {
            existingWindow.Close();
        }
    }

    private static void OnRenderingFailed(Exception exception) =>
        new RuntimeHealthJournal().Record(RuntimeHealthEvent.PopupRenderFailed, exception);

    private void OnPinStateChanged(PopupWindow window, bool isPinned)
    {
        PinStateChanged?.Invoke(window.RequestId, isPinned);
        if (isPinned)
        {
            if (_transientRequestId == window.RequestId)
            {
                _transientRequestId = null;
            }

            return;
        }

        if (window.IsVisible)
        {
            // Unpinning keeps this result visible, but returns it to the normal
            // transient lifecycle so the next outside click can dismiss it.
            CloseOtherTransient(window.RequestId);
            _transientRequestId = window.RequestId;
        }
    }

    private void OnRetranslateRequested(PopupWindow window, string targetLanguage)
    {
        RetranslateRequested?.Invoke(new PopupRetranslateRequest(
            window.RequestId,
            window.LanguageSwitchSourceText,
            window.CurrentTargetLanguage,
            targetLanguage,
            window.AnchorPoint));
    }

    private bool OnCorrectionSaveRequested(PopupWindow window, string correctedTranslation)
    {
        return TranslationCorrectionRequested?.Invoke(new PopupTranslationCorrection(
            window.RequestId,
            window.CurrentTranslationSourceText,
            correctedTranslation,
            window.CurrentSourceLanguage,
            window.CurrentTargetLanguage)) == true;
    }

    private void OnExplanationRequested(
        PopupWindow window,
        string subjectText,
        ExplanationScope scope)
    {
        if (string.IsNullOrWhiteSpace(subjectText)
            || string.IsNullOrWhiteSpace(window.CurrentTranslationSourceText)
            || string.IsNullOrWhiteSpace(window.CurrentTranslationText))
        {
            return;
        }

        ExplanationRequested?.Invoke(new PopupExplanationRequest(
            window.RequestId,
            window.HistorySessionId,
            window.HistoryCreatedAt,
            subjectText,
            window.CurrentTranslationSourceText,
            window.CurrentTranslationText,
            window.CurrentSourceLanguage,
            window.CurrentTargetLanguage,
            scope));
    }

    private void OnExplanationDismissed(PopupWindow window)
    {
        ExplanationDismissed?.Invoke(window.RequestId);
    }

    private async void OnManualExplanationRecordRequested(PopupWindow window)
    {
        var version = window.BeginExplanationRecordSave();
        if (version == 0)
        {
            return;
        }

        if (_manualRecordWriter is null)
        {
            window.MarkExplanationRecordFailed("unavailable", version);
            return;
        }

        var request = new ManualExplanationRecordRequest(
            DateTimeOffset.Now,
            window.HistorySessionId,
            window.UiLanguage,
            window.CurrentTranslationSourceText,
            window.CurrentTranslationText,
            window.CurrentSourceLanguage,
            window.CurrentTargetLanguage,
            window.CurrentExplanationSubjectText,
            window.CurrentExplanationScope,
            window.CurrentExplanationText);
        AiHistoryWriteResult result;
        try
        {
            result = await _manualRecordWriter(request, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            result = new AiHistoryWriteResult(false, "write_failed");
        }
        catch (Exception)
        {
            result = new AiHistoryWriteResult(false, "write_failed");
        }

        if (!_windows.TryGetValue(window.RequestId, out var current)
            || !ReferenceEquals(current, window))
        {
            return;
        }

        if (result.Saved)
        {
            window.MarkExplanationRecorded(version);
        }
        else
        {
            window.MarkExplanationRecordFailed(result.ErrorCode, version);
        }
    }

    private void OnInlineQuestionSubmitted(
        PopupWindow window,
        string question,
        QuestionContextKind contextKind)
    {
        if (string.IsNullOrWhiteSpace(question)
            || string.IsNullOrWhiteSpace(window.CurrentTranslationSourceText)
            || string.IsNullOrWhiteSpace(window.CurrentTranslationText))
        {
            return;
        }

        var context = new QuestionContextSnapshot(
            window.RequestId,
            window.HistoryCreatedAt,
            window.CurrentTranslationSourceText,
            window.CurrentTranslationText,
            contextKind == QuestionContextKind.Explanation
                ? window.CurrentExplanationText
                : null,
            window.CurrentSourceLanguage,
            window.CurrentTargetLanguage,
            window.UiLanguage,
            contextKind);
        _questionContexts[window.HistorySessionId] = context;
        var questionWindow = GetOrCreateQuestionWindow(window.HistorySessionId);
        questionWindow.ShowNear(window);
        StartQuestion(questionWindow, context, question, isRetry: false);
    }

    private void OnDeepSeekChatRequested(PopupWindow parent)
    {
        var appearance = _getAppearanceSettings();
        _questionContexts[_deepSeekChatSessionId] = new QuestionContextSnapshot(
            ParentRequestId: 0,
            HistoryCreatedAt: DateTimeOffset.Now,
            SourceText: string.Empty,
            TranslationText: string.Empty,
            ExplanationText: null,
            SourceLanguage: string.Empty,
            TargetLanguage: string.Empty,
            UiLanguage: appearance.UiLanguage,
            ContextKind: QuestionContextKind.GeneralChat);

        var chatWindow = GetOrCreateQuestionWindow(
            _deepSeekChatSessionId,
            QuestionAnswerWindowMode.DeepSeekQuickChat);
        chatWindow.SetPinned(true);
        if (!chatWindow.IsVisible)
        {
            chatWindow.ShowNear(parent);
        }

        chatWindow.FocusQuestionInput();
    }

    private QuestionAnswerWindow GetOrCreateQuestionWindow(
        Guid sessionId,
        QuestionAnswerWindowMode mode = QuestionAnswerWindowMode.Contextual)
    {
        if (_questionWindows.TryGetValue(sessionId, out var existing))
        {
            return existing;
        }

        var appearance = _getAppearanceSettings();
        var window = new QuestionAnswerWindow(
            sessionId,
            appearance.UiLanguage,
            appearance.EnglishFontFamily,
            appearance.ChineseFontFamily,
            mode);
        window.QuestionSubmitted += OnQuestionWindowSubmitted;
        window.StopRequested += OnQuestionWindowStopRequested;
        window.RetryRequested += OnQuestionWindowRetryRequested;
        window.ManualRecordRequested += OnQuestionWindowManualRecordRequested;
        window.Closed += OnQuestionWindowClosed;
        _questionWindows.Add(sessionId, window);
        return window;
    }

    private void OnQuestionWindowSubmitted(QuestionAnswerWindow window, string question)
    {
        if (_questionContexts.TryGetValue(window.SessionId, out var context))
        {
            StartQuestion(window, context, question, isRetry: false);
        }
    }

    private void OnQuestionWindowStopRequested(QuestionAnswerWindow window)
    {
        QuestionAnswerCancelled?.Invoke(window.SessionId);
    }

    private void OnQuestionWindowRetryRequested(QuestionAnswerWindow window)
    {
        if (_questionContexts.TryGetValue(window.SessionId, out var context)
            && !string.IsNullOrWhiteSpace(window.PendingQuestion))
        {
            StartQuestion(window, context, window.PendingQuestion, isRetry: true);
        }
    }

    private async void OnQuestionWindowManualRecordRequested(QuestionAnswerWindow window)
    {
        if (!_questionContexts.TryGetValue(window.SessionId, out var context))
        {
            window.MarkManualRecordFailed("unavailable");
            return;
        }

        var turns = window.CompletedTurns;
        var recordedTurnCount = window.BeginManualRecordSave();
        if (recordedTurnCount == 0)
        {
            return;
        }

        if (_manualRecordWriter is null)
        {
            window.MarkManualRecordFailed("unavailable");
            return;
        }

        var request = new ManualConversationRecordRequest(
            DateTimeOffset.Now,
            window.SessionId,
            window.UiLanguage,
            context.SourceText,
            context.TranslationText,
            context.SourceLanguage,
            context.TargetLanguage,
            context.ExplanationText,
            context.ContextKind,
            turns);
        AiHistoryWriteResult result;
        try
        {
            result = await _manualRecordWriter(request, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            result = new AiHistoryWriteResult(false, "write_failed");
        }
        catch (Exception)
        {
            result = new AiHistoryWriteResult(false, "write_failed");
        }

        if (!_questionWindows.TryGetValue(window.SessionId, out var current)
            || !ReferenceEquals(current, window))
        {
            return;
        }

        if (result.Saved)
        {
            window.MarkManualRecordSaved(recordedTurnCount);
        }
        else
        {
            window.MarkManualRecordFailed(result.ErrorCode);
        }
    }

    private void StartQuestion(
        QuestionAnswerWindow window,
        QuestionContextSnapshot context,
        string question,
        bool isRetry)
    {
        if (window.IsStreaming)
        {
            QuestionAnswerCancelled?.Invoke(window.SessionId);
            window.MarkStopped();
        }

        var history = window.CompletedTurns;
        window.BeginQuestion(question, isRetry);
        if (QuestionAnswerRequested is not { } requested)
        {
            window.FailAnswer(context.UiLanguage == UiLanguageCatalog.SimplifiedChineseLanguageId
                ? "AI 问答暂不可用，请重试。"
                : "AI Q&A is unavailable. Try again.");
            return;
        }

        requested(new PopupQuestionAnswerRequest(
            window.SessionId,
            context.ParentRequestId,
            context.HistoryCreatedAt,
            question,
            context.SourceText,
            context.TranslationText,
            context.ExplanationText,
            context.SourceLanguage,
            context.TargetLanguage,
            context.UiLanguage,
            context.ContextKind,
            history));
    }

    private void OnQuestionWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not QuestionAnswerWindow window)
        {
            return;
        }

        window.QuestionSubmitted -= OnQuestionWindowSubmitted;
        window.StopRequested -= OnQuestionWindowStopRequested;
        window.RetryRequested -= OnQuestionWindowRetryRequested;
        window.ManualRecordRequested -= OnQuestionWindowManualRecordRequested;
        window.Closed -= OnQuestionWindowClosed;
        _questionWindows.Remove(window.SessionId);
        _questionContexts.Remove(window.SessionId);
        QuestionAnswerCancelled?.Invoke(window.SessionId);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not PopupWindow window)
        {
            return;
        }

        window.PinStateChanged -= OnPinStateChanged;
        window.PlacementChanged -= _placementStore.Save;
        window.RenderingFailed -= OnRenderingFailed;
        window.RetranslateRequested -= OnRetranslateRequested;
        window.CorrectionSaveRequested -= OnCorrectionSaveRequested;
        window.ExplanationRequested -= OnExplanationRequested;
        window.ExplanationDismissed -= OnExplanationDismissed;
        window.ManualExplanationRecordRequested -= OnManualExplanationRecordRequested;
        window.QuestionSubmitted -= OnInlineQuestionSubmitted;
        window.DeepSeekChatRequested -= OnDeepSeekChatRequested;
        window.Closed -= OnWindowClosed;
        _windows.Remove(window.RequestId);
        if (_transientRequestId == window.RequestId)
        {
            _transientRequestId = null;
        }

        if (_questionWindows.TryGetValue(window.HistorySessionId, out var questionWindow)
            && !questionWindow.IsPinned)
        {
            questionWindow.Close();
        }

        if (!_disposing)
        {
            PopupClosed?.Invoke(window.RequestId);
        }
    }

}

internal sealed record PopupAppearanceSettings(
    double FontSize,
    string EnglishFontFamily,
    string ChineseFontFamily,
    string UiLanguage,
    string PopupVisualStyle)
{
    internal static PopupAppearanceSettings Default { get; } = new(
        16.5,
        "Times New Roman",
        "SimHei",
        "en",
        PopupVisualStyleCatalog.DefaultStyleId);
}

internal sealed record QuestionContextSnapshot(
    long ParentRequestId,
    DateTimeOffset HistoryCreatedAt,
    string SourceText,
    string TranslationText,
    string? ExplanationText,
    string SourceLanguage,
    string TargetLanguage,
    string UiLanguage,
    QuestionContextKind ContextKind);
