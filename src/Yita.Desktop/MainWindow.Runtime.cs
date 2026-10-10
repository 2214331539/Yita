using Yita.Core;
using Yita.Core.Platform;
using Yita.Core.Placement;
using Yita.Core.Selection;
using Yita.Services;
using Yita.Settings;
using Yita.Translation;

namespace Yita.Desktop;

public sealed partial class MainWindow
{
    private readonly LatestRequestController _clipboardRequests = new();
    private bool _desktopSessionActive = true;
    private long _desktopSessionGeneration;
    private readonly HashSet<Avalonia.Controls.Window> _suspendedWindows = new();

    private void OnDesktopSessionActivityChanged(object? sender, bool active) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => SetDesktopSessionActive(active));

    internal void SetDesktopSessionActive(bool active)
    {
        if (_shuttingDown || _desktopSessionActive == active) return;
        _desktopSessionActive = active;
        _desktopSessionGeneration++;
        if (!active)
        {
            _clipboardRequests.Cancel();
            foreach (var popup in _popups.ToArray())
            {
                popup.SuspendSession();
                if (popup.IsVisible && popup.IsPinned) { _suspendedWindows.Add(popup); popup.Hide(); }
                else popup.DismissIfUnpinned();
            }
            foreach (var conversation in _conversations.ToArray())
            {
                conversation.SuspendSession();
                if (conversation.IsVisible && conversation.IsPinned) { _suspendedWindows.Add(conversation); conversation.Hide(); }
                else conversation.Close();
            }
        }
        else
        {
            foreach (var window in _suspendedWindows.ToArray()) DesktopFloatingWindowBehavior.Restore(window);
            _suspendedWindows.Clear();
        }
    }
    private void OnSelectionCaptured(object? sender, SelectionCapturedEventArgs args) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (_shuttingDown || !_desktopSessionActive || _selectionRuntime is IDesktopSessionRuntime { IsSessionActive: false }) return;
            try { await ShowSelectionTranslationAsync(args.Request, args.Result, args.ReadDuration); }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError("Selection presentation failed: {0}", exception.GetType().Name);
            }
        });

    private void OnExternalPointerPressed(object? sender, ScreenPoint point) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(DismissUnpinnedWindows);

    internal async void TranslateClipboardFromTray()
    {
        try
        {
            if (_shuttingDown || !_desktopSessionActive) return;
            if (_selectionRuntime is not null) { _selectionRuntime.TranslateClipboard(); return; }
            using var pending = _clipboardRequests.Begin();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var text = Clipboard is { } clipboard ? await clipboard.GetTextAsync() : null;
            var duration = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (_shuttingDown || !_desktopSessionActive || !pending.IsCurrent) return;
            var area = Screens.Primary?.WorkingArea;
            var pointer = area is { } screen
                ? new ScreenPoint(screen.X + screen.Width / 2d, screen.Y + screen.Height / 2d)
                : new ScreenPoint(Position.X, Position.Y);
            var result = string.IsNullOrWhiteSpace(text)
                ? SelectionResult.Failed(SelectionFailureKind.Empty, "manual-clipboard-empty")
                : new SelectionResult(text, SelectionSource.ManualClipboard);
            await ShowSelectionTranslationAsync(new SelectionRequest(SelectionTrigger.TrayCommand, pointer), result, duration);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Clipboard translation failed: {0}", exception.GetType().Name);
        }
    }

    private readonly HashSet<TranslationPopupWindow> _popups = new();
    private readonly HashSet<QuestionAnswerWindow> _conversations = new();
    private readonly Dictionary<TranslationPopupWindow, PopupSession> _sessions = new();
    private TranslationPopupWindow? _popup;
    private readonly TranslationPerformanceMonitor _performance = new();
    private readonly SelectionDiagnostics _selectionDiagnostics = new();
    internal SelectionDiagnostics SelectionDiagnostics => _selectionDiagnostics;
    internal IReadOnlyCollection<TranslationPopupWindow> TranslationPopups => _popups;

    internal async Task ShowSelectionTranslationAsync(SelectionRequest request, SelectionResult result, TimeSpan? readDuration = null)
    {
        TranslationPopupWindow? popup = null;
        try
        {
            if (_shuttingDown || !_desktopSessionActive) return;
            var sessionGeneration = _desktopSessionGeneration;
            await InitializeAsync();
            if (_shuttingDown || !_desktopSessionActive || sessionGeneration != _desktopSessionGeneration
                || (!_settings.IsEnabled && request.Trigger == SelectionTrigger.MouseGesture)) return;
            var diagnostic = _selectionDiagnostics.Record(request.Trigger, result, readDuration);
            UpdateSelectionStatus();
            if (!result.Succeeded && request.Trigger == SelectionTrigger.MouseGesture) return;
            if (diagnostic.Issue == SelectionIssue.Cancelled) return;
            if (_popup is null || _popup.IsPinned) _popup = CreatePopup();
            popup = _popup;
            var settings = _settings.ToOriginal(_savedApiKey) with { UiLanguage = _uiLanguage };
            popup.ApplySettings(settings);
            popup.PlaceNear(request, result, SavedPopupOffset());
            if (!result.Succeeded)
            {
                popup.TranslationRequests.Cancel();
                _sessions.Remove(popup);
                popup.BeginTranslation("");
                if (!popup.IsVisible) popup.Show();
                popup.SetSelectionError(diagnostic.Issue);
                _performance.Begin(request.Trigger == SelectionTrigger.MouseGesture ? TranslationTrigger.Selection : TranslationTrigger.Clipboard,
                    settings.ProviderId).Complete(TranslationOutcome.NoSelection);
                return;
            }
            await TranslatePopupAsync(popup, result.Text!, result.Context, settings,
                request.Trigger == SelectionTrigger.MouseGesture ? TranslationTrigger.Selection : TranslationTrigger.Clipboard);
        }
        catch (Exception exception)
        {
            if (!_shuttingDown && popup is { IsVisible: true }) popup.SetError(exception.Message);
        }
    }

    private async Task TranslatePopupAsync(TranslationPopupWindow popup, string text, string? context,
        AppSettings settings, TranslationTrigger trigger, bool preserveSize = false)
    {
        using var pending = popup.TranslationRequests.Begin();
        var performance = _performance.Begin(trigger, settings.ProviderId);
        var outcome = TranslationOutcome.Cancelled;
        try
        {
            _sessions.Remove(popup);
            popup.SetTargetLanguage(settings.TargetLanguageMode, settings.TargetLanguage);
            popup.BeginTranslation(text, preserveSize);
            if (!popup.IsVisible) popup.Show();
            var source = Yita.Selection.TextNormalizer.Normalize(text);
            if (source.Length > settings.MaximumSelectionCharacters)
                throw new ArgumentException(Localize("The selection exceeds the character limit.", "所选文本超过字符上限，请缩小选区。"));
            var translation = _translationRuntime.Prepare(source, context, settings);
            var session = new PopupSession(settings, translation, text,
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
            if (!_shuttingDown && pending.IsCurrent && popup.IsVisible)
                popup.SetError(exception.Message);
        }
        finally { performance.Complete(outcome); }
    }

    internal async Task ChangePopupTargetLanguageAsync(TranslationPopupWindow popup, string choice)
    {
        if (_shuttingDown || !_desktopSessionActive || !_popups.Contains(popup) || !popup.IsVisible
            || !TargetLanguageOptions.Labels.Any(option => option.Value == choice)) return;
        try
        {
            if (_settingsFailure is not null)
            {
                if (_sessions.TryGetValue(popup, out var unchanged))
                    popup.SetTargetLanguage(unchanged.Settings.TargetLanguageMode, unchanged.Settings.TargetLanguage);
                else popup.SetTargetLanguage(_settings.TargetLanguageMode, _settings.TargetLanguage);
                popup.ShowNotice(Localize("Settings are read-only; the default language cannot be changed.", "设置只读，无法更改默认目标语言。"));
                return;
            }
            var mode = choice == TargetLanguageOptions.Automatic ? "auto" : "fixed";
            var language = mode == "auto" ? "简体中文" : choice;
            _settings = _settings with { TargetLanguageMode = mode, TargetLanguage = language };
            SetCombo(TargetLanguageComboBox, choice);
            var saved = PersistSettingsWithStatusAsync();
            if (_sessions.TryGetValue(popup, out var session))
            {
                var settings = session.Settings with { TargetLanguageMode = mode, TargetLanguage = language, UiLanguage = _uiLanguage };
                await TranslatePopupAsync(popup, session.SourceText, session.Request.Context, settings,
                    TranslationTrigger.Retranslation, preserveSize: true);
            }
            if (!await saved && popup.IsVisible && popup.TargetLanguageChoice == choice)
                popup.ShowNotice(Localize("Language changed for now; could not save the default.", "已切换语言，但默认设置保存失败。"));
        }
        catch (Exception exception)
        {
            if (!_shuttingDown && popup.IsVisible) popup.ShowNotice(exception.Message);
        }
    }

    private TranslationPopupWindow CreatePopup()
    {
        var popup = new TranslationPopupWindow();
        _popups.Add(popup);
        popup.TargetLanguageChanged += async (_, choice) => await ChangePopupTargetLanguageAsync(popup, choice);
        popup.Moved += (_, args) =>
        {
            _settings = _settings with { PopupOffsetX = args.Offset.X, PopupOffsetY = args.Offset.Y };
            _ = PersistSettingsWithStatusAsync();
        };
        popup.Closed += (_, _) =>
        {
            _popups.Remove(popup);
            _suspendedWindows.Remove(popup);
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
        if (_shuttingDown || !_desktopSessionActive || !_sessions.TryGetValue(popup, out var session)) return;
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
        if (_shuttingDown || !_desktopSessionActive || !_sessions.TryGetValue(popup, out var session)) return;
        var conversation = new QuestionAnswerWindow(session.Settings, session.Request.Text, session.Translation,
            session.Explanation, kind,
            _translationRuntime, _records, session.Context);
        _conversations.Add(conversation);
        conversation.Closed += (_, _) => { _conversations.Remove(conversation); _suspendedWindows.Remove(conversation); };
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
            if (_permissionService is not null) await RefreshPlatformPermissionsAsync();
            if (Clipboard is { } clipboard)
                await clipboard.SetTextAsync(CreatePlatformDiagnostics() + "\n\n"
                    + _performance.CreateReport(_settings.UiLanguage == "zh-CN") + "\n\n"
                    + _selectionDiagnostics.CreateReport(_settings.UiLanguage == "zh-CN") + "\n\n"
                    + (_selectionRuntime?.CreateDiagnostics(_settings.UiLanguage == "zh-CN")
                    ?? Localize("Input capture is unavailable.", "划词捕获不可用。")));
        }
        catch { }
    }

    internal string CreatePlatformDiagnostics() => string.Join("\n", new[]
    {
        "Yita " + _platform.PlatformName + " Avalonia preview",
        "Automatic selection: " + (_selectionRuntime is null ? "not implemented" : _selectionRuntime.IsRunning ? "running" : "unavailable"),
        "Global shortcut: " + (_selectionRuntime?.IsHotkeyRunning == true ? "running" : "unavailable"),
        "Login startup: " + (_startupRegistration.IsSupported ? "available" : "not implemented"),
        "Settings schema: " + _settings.SchemaVersion,
        "Settings writable: " + (_settingsFailure is null ? "yes" : "no (" + _settingsFailure + ")"),
        "Credential storage: " + (_credentialsAvailable ? "readable" : "unavailable"),
        "Permission helper: " + (_permissionStatus?.Service.ToString() ?? "not configured"),
        "Accessibility: " + (_permissionStatus?.Permissions.Accessibility == true ? "granted" : "unavailable"),
        "Input monitoring: " + (_permissionStatus?.Permissions.InputMonitoring == true ? "granted" : "unavailable"),
        "Mac app location: " + (OperatingSystem.IsMacOS()
            ? Yita.Native.Mac.MacApplicationBundle.GetLocation(Environment.ProcessPath,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).ToString() : "not applicable"),
        "Encrypted memory: " + (_memoryInitializationFailed || _memory?.LoadFailed == true ? "unreadable"
            : _memory is null ? "unavailable" : "available"),
    });

    private void UpdateSelectionStatus()
    {
        var last = _selectionDiagnostics.LastMeaningful;
        SelectionStatusText.IsVisible = last is not null && SelectionIssueClassifier.IsActionable(last.Issue);
        SelectionStatusText.Text = last is not null
            ? SelectionFailureText.Message(last.Issue, _uiLanguage == "zh-CN") : "";
    }

    internal void DismissUnpinnedWindows()
    {
        if (_shuttingDown) return;
        foreach (var popup in _popups.ToArray()) popup.DismissIfUnpinned();
        foreach (var conversation in _conversations.ToArray()) if (!conversation.IsPinned) conversation.Close();
    }

    internal async void ShowAbout()
    {
        try { await ShowMessageAsync("Yita · 译獭\n" + new DesktopPlatformServices().PlatformName
            + Localize(" Avalonia preview", " Avalonia 预览版")); }
        catch { }
    }

    internal void ShutdownServices()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        if (_selectionRuntime is not null)
        {
            _selectionRuntime.SelectionCaptured -= OnSelectionCaptured;
            _selectionRuntime.ExternalPointerPressed -= OnExternalPointerPressed;
            if (_selectionRuntime is IDesktopSessionRuntime session) session.SessionActivityChanged -= OnDesktopSessionActivityChanged;
            if (_selectionRuntime is Yita.Native.Mac.MacSelectionRuntime mac) mac.StatusChanged -= OnNativeInputStatusChanged;
        }
        _connectionRequests.Dispose();
        _summaryRequests.Dispose();
        _clipboardRequests.Dispose();
        _permissionRequests.Dispose();
        (_permissionService as IDisposable)?.Dispose();
        foreach (var popup in _popups.ToArray()) popup.Close();
        foreach (var conversation in _conversations.ToArray()) conversation.Close();
        _suspendedWindows.Clear();
        (_providerFactory as IDisposable)?.Dispose();
        _memoryProtector?.Dispose();
        _injectedClient?.Dispose();
        ApiKeyPasswordBox.Text = "";
        _savedApiKey = "";
    }

    private sealed class PopupSession(AppSettings settings, TranslationRequest request, string sourceText, AiHistoryContext context)
    {
        internal AppSettings Settings { get; set; } = settings;
        internal TranslationRequest Request { get; } = request;
        internal string SourceText { get; } = sourceText;
        internal AiHistoryContext Context { get; } = context;
        internal string Translation { get; set; } = "";
        internal string Explanation { get; set; } = "";
        internal ExplanationRequest? ExplanationRequest { get; set; }
    }
}
