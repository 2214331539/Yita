using Yita.Core;
using Yita.Core.Placement;
using Yita.Core.Selection;
using Yita.Services;
using Yita.Settings;
using Yita.Translation;

namespace Yita.Desktop;

public sealed partial class MainWindow
{
    private readonly HashSet<TranslationPopupWindow> _popups = new();
    private readonly HashSet<QuestionAnswerWindow> _conversations = new();
    private readonly Dictionary<TranslationPopupWindow, PopupSession> _sessions = new();
    private TranslationPopupWindow? _popup;
    private readonly TranslationPerformanceMonitor _performance = new();
    internal IReadOnlyCollection<TranslationPopupWindow> TranslationPopups => _popups;

    internal async Task ShowSelectionTranslationAsync(SelectionRequest request, SelectionResult result)
    {
        TranslationPopupWindow? popup = null;
        LatestRequestController.RequestLease? pending = null;
        TranslationPerformanceOperation? performance = null;
        var outcome = TranslationOutcome.Cancelled;
        try
        {
            if (_shuttingDown) return;
            await InitializeAsync();
            if (_shuttingDown || (!_settings.IsEnabled && request.Trigger == SelectionTrigger.MouseGesture)) return;
            if (!result.Succeeded && request.Trigger == SelectionTrigger.MouseGesture) return;
            if (_popup is null || _popup.IsPinned) _popup = CreatePopup();
            popup = _popup;
            pending = popup.TranslationRequests.Begin();
            var settings = _settings.ToOriginal(_savedApiKey) with { UiLanguage = _uiLanguage };
            performance = _performance.Begin(request.Trigger == SelectionTrigger.MouseGesture ? TranslationTrigger.Selection : TranslationTrigger.Clipboard, settings.ProviderId);
            popup.ApplySettings(settings);
            popup.PlaceNear(request, result, SavedPopupOffset());
            popup.BeginTranslation(result.Text ?? "");
            if (!popup.IsVisible) popup.Show();
            if (!result.Succeeded)
            {
                popup.SetError(Localize("No text captured. Copy text and try again.", "未读取到文字，请复制文字后重试。"));
                outcome = TranslationOutcome.NoSelection;
                return;
            }
            var source = Yita.Selection.TextNormalizer.Normalize(result.Text!);
            if (source.Length > settings.MaximumSelectionCharacters)
                throw new ArgumentException(Localize("The selection exceeds the character limit.", "所选文本超过字符上限，请缩小选区。"));
            var translation = _translationRuntime.Prepare(source, result.Context, settings);
            var session = new PopupSession(settings, translation,
                new AiHistoryContext(Guid.NewGuid(), DateTimeOffset.UtcNow, source, "", translation.SourceLanguage, translation.TargetLanguage));
            _sessions[popup] = session;
            await foreach (var output in _translationRuntime.TranslateAsync(translation, settings, pending.Token, performance))
            {
                if (!pending.IsCurrent) return;
                session.Translation = output;
                popup.SetText(output);
            }
            if (pending.IsCurrent) { popup.CompleteTranslation(); outcome = TranslationOutcome.Succeeded; }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            outcome = TranslationOutcome.Failed;
            if (!_shuttingDown && pending is not { IsCurrent: false } && popup is { IsVisible: true })
                popup.SetError(exception.Message);
        }
        finally { performance?.Complete(outcome); pending?.Dispose(); }
    }

    private TranslationPopupWindow CreatePopup()
    {
        var popup = new TranslationPopupWindow();
        _popups.Add(popup);
        popup.Moved += (_, args) =>
        {
            _settings = _settings with { PopupOffsetX = args.Offset.X, PopupOffsetY = args.Offset.Y };
            _ = PersistSettingsWithStatusAsync();
        };
        popup.Closed += (_, _) =>
        {
            _popups.Remove(popup);
            _sessions.Remove(popup);
            if (ReferenceEquals(_popup, popup)) _popup = null;
        };
        popup.Dismissed += (_, _) => { if (!ReferenceEquals(_popup, popup)) popup.Close(); };
        popup.ExplanationRequested += async (_, args) => await ExplainAsync(popup, args);
        popup.QuestionRequested += (_, args) => OpenConversation(popup, args.Question, args.ContextKind);
        popup.CorrectionSaved += (_, text) =>
        {
            try
            {
                if (_memory is null) throw new InvalidOperationException(Localize("Encrypted memory is unavailable.", "加密翻译记忆不可用。"));
                if (!_sessions.TryGetValue(popup, out var session)) return;
                _memory.AddOrUpdate(session.Request.Text, text, session.Request.SourceLanguage, session.Request.TargetLanguage);
                session.Translation = text;
                popup.SetText(text);
                popup.ShowNotice(Localize("Correction saved.", "已保存修正。"));
                UpdateMemoryStatus();
            }
            catch (Exception exception) { popup.ShowNotice(exception.Message); }
        };
        popup.RecordExplanationRequested += async (_, _) =>
        {
            if (!_sessions.TryGetValue(popup, out var session) || session.ExplanationRequest is not { } explanation) return;
            var version = popup.BeginExplanationRecord();
            if (version == 0) return;
            try
            {
                var result = await _records.AppendManualRecordAsync(_settings.ToOriginal(_savedApiKey),
                    new ManualExplanationRecordRequest(DateTimeOffset.Now, session.Context.SessionId, session.Settings.UiLanguage,
                        session.Request.Text, session.Translation, session.Request.SourceLanguage, session.Request.TargetLanguage,
                        explanation.SubjectText, explanation.Scope, session.Explanation));
                if (!popup.CompleteExplanationRecord(version, result.Saved)) return;
                popup.ShowNotice(result.Saved ? Localize("Recorded.", "已记录。") : Localize("Could not record. Check your record folder.", "无法记录，请检查记录目录。"));
            }
            catch (Exception exception) { if (popup.CompleteExplanationRecord(version, false)) popup.ShowNotice(exception.Message); }
        };
        return popup;
    }

    private async Task ExplainAsync(TranslationPopupWindow popup, ExplanationAction action)
    {
        if (_shuttingDown || !_sessions.TryGetValue(popup, out var session)) return;
        using var pending = popup.ExplanationRequests.Begin();
        popup.BeginExplanation();
        var request = new ExplanationRequest(action.Subject, session.Request.Text, session.Translation,
            session.Request.SourceLanguage, session.Request.TargetLanguage, action.Scope);
        session.ExplanationRequest = request;
        session.Explanation = "";
        try
        {
            await foreach (var output in _translationRuntime.ExplainAsync(request, session.Settings, pending.Token))
            {
                if (!pending.IsCurrent) return;
                session.Explanation = output;
                popup.SetExplanation(output);
            }
            if (!pending.IsCurrent) return;
            if (session.Explanation.Length == 0) throw new InvalidOperationException(Localize("No explanation returned.", "未返回解释。"));
            popup.CompleteExplanation();
            var result = await _records.AppendExplanationAsync(session.Settings, session.Context with { TranslationText = session.Translation },
                request.SubjectText, request.Scope, session.Explanation, DateTimeOffset.Now, pending.Token);
            if (pending.IsCurrent && !result.Saved && result.ErrorCode is not null)
                popup.ShowNotice(Localize("Could not save the AI record.", "AI 记录保存失败。"));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (pending.IsCurrent) popup.SetExplanationError(exception.Message); }
    }

    private void OpenConversation(TranslationPopupWindow popup, string question, QuestionContextKind kind)
    {
        if (_shuttingDown || !_sessions.TryGetValue(popup, out var session)) return;
        var conversation = new QuestionAnswerWindow(session.Settings, session.Request.Text, session.Translation,
            session.Explanation, kind,
            _translationRuntime, _records, session.Context);
        _conversations.Add(conversation);
        conversation.Closed += (_, _) => _conversations.Remove(conversation);
        conversation.Position = new Avalonia.PixelPoint(popup.Position.X + 24, popup.Position.Y + 24);
        conversation.Show();
        if (!string.IsNullOrWhiteSpace(question)) _ = conversation.AskAsync(question);
    }

    private PopupOffset? SavedPopupOffset() => _settings.PopupOffsetX is double x && _settings.PopupOffsetY is double y
        ? new PopupOffset(x, y) : null;

    internal async void CopyDiagnostics()
    {
        try
        {
            if (Clipboard is { } clipboard)
                await clipboard.SetTextAsync(_performance.CreateReport(_settings.UiLanguage == "zh-CN") + "\n\n"
                    + (_windowsRuntime?.CreateDiagnostics(_settings.UiLanguage == "zh-CN")
                    ?? Localize("Input capture is unavailable.", "划词捕获不可用。")));
        }
        catch { }
    }

    internal void DismissUnpinnedWindows()
    {
        if (_shuttingDown) return;
        foreach (var popup in _popups.ToArray()) popup.DismissIfUnpinned();
        foreach (var conversation in _conversations.ToArray()) if (!conversation.IsPinned) conversation.Close();
    }

    internal async void ShowAbout()
    {
        try { await ShowMessageAsync("Yita · 译獭\n" + Localize("Windows Avalonia preview", "Windows Avalonia 预览版")); }
        catch { }
    }

    internal void ShutdownServices()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        _connectionRequests.Dispose();
        _summaryRequests.Dispose();
        foreach (var popup in _popups.ToArray()) popup.Close();
        foreach (var conversation in _conversations.ToArray()) conversation.Close();
        (_providerFactory as IDisposable)?.Dispose();
        _injectedClient?.Dispose();
        ApiKeyPasswordBox.Text = "";
        _savedApiKey = "";
    }

    private sealed class PopupSession(AppSettings settings, TranslationRequest request, AiHistoryContext context)
    {
        internal AppSettings Settings { get; set; } = settings;
        internal TranslationRequest Request { get; } = request;
        internal AiHistoryContext Context { get; } = context;
        internal string Translation { get; set; } = "";
        internal string Explanation { get; set; } = "";
        internal ExplanationRequest? ExplanationRequest { get; set; }
    }
}
