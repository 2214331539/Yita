using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Yita.Hooks;
using Yita.Models;
using Yita.Selection;
using Yita.Settings;
using Yita.Translation;
using Yita.Windows;

namespace Yita.Services;

internal sealed class SelectionTranslationCoordinator : IDisposable
{
    private static readonly TimeSpan SelectionReadTimeout = TimeSpan.FromMilliseconds(4000);
    private static readonly TimeSpan TranslationTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FirstContentTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExplanationTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ExplanationFirstContentTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExplanationStreamIdleTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan QuestionAnswerTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan QuestionAnswerFirstContentTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan QuestionAnswerStreamIdleTimeout = TimeSpan.FromSeconds(15);
    private const int PromptVersion = 5;

    private readonly GlobalMouseHook _mouseHook;
    private readonly ISelectionReader _selectionReader;
    private readonly ITranslationProviderFactory _translationProviderFactory;
    private readonly IPopupPresenter _popupPresenter;
    private readonly Dispatcher _dispatcher;
    private readonly Func<AppSettings> _getSettings;
    private readonly TranslationPerformanceMonitor _performanceMonitor;
    private readonly TranslationMemoryStore _translationMemoryStore;
    private readonly AiHistoryStore _aiHistoryStore;
    private readonly RequestVersionGate _requestGate = new();
    private readonly TranslationMemoryCache _translationCache = new();
    private readonly TranslationInFlightRegistry _inFlightTranslations = new();
    private readonly SemaphoreSlim _translationConcurrency = new(3, 3);
    private readonly SemaphoreSlim _explanationConcurrency = new(1, 1);
    private readonly object _retranslationSync = new();
    private readonly ExplanationRequestGate _explanationGate = new();
    private readonly QuestionAnswerRequestGate _questionAnswerGate = new();
    private readonly SemaphoreSlim _questionAnswerConcurrency = new(2, 2);
    private readonly Dictionary<long, CancellationTokenSource> _retranslations = new();
    private readonly Dictionary<long, CancellationTokenSource> _detachedSelections = new();
    private readonly HashSet<long> _runningSelections = new();
    private bool _disposed;

    public SelectionTranslationCoordinator(
        GlobalMouseHook mouseHook,
        ISelectionReader selectionReader,
        ITranslationProviderFactory translationProviderFactory,
        IPopupPresenter popupPresenter,
        Dispatcher dispatcher,
        Func<AppSettings> getSettings,
        TranslationPerformanceMonitor? performanceMonitor = null,
        TranslationMemoryStore? translationMemoryStore = null,
        AiHistoryStore? aiHistoryStore = null)
    {
        _mouseHook = mouseHook;
        _selectionReader = selectionReader;
        _translationProviderFactory = translationProviderFactory;
        _popupPresenter = popupPresenter;
        _dispatcher = dispatcher;
        _getSettings = getSettings;
        _performanceMonitor = performanceMonitor ?? new TranslationPerformanceMonitor();
        _translationMemoryStore = translationMemoryStore ?? new TranslationMemoryStore();
        _aiHistoryStore = aiHistoryStore ?? new AiHistoryStore();

        _mouseHook.MousePressed += OnMousePressed;
        _mouseHook.SelectionGestureCompleted += OnSelectionGestureCompleted;
        _popupPresenter.RetranslateRequested += OnRetranslateRequested;
        _popupPresenter.PopupClosed += OnPopupClosed;
        _popupPresenter.PinStateChanged += OnPopupPinStateChanged;
        _popupPresenter.TranslationCorrectionRequested += OnTranslationCorrectionRequested;
        _popupPresenter.ExplanationRequested += OnExplanationRequested;
        _popupPresenter.ExplanationDismissed += OnExplanationDismissed;
        _popupPresenter.QuestionAnswerRequested += OnQuestionAnswerRequested;
        _popupPresenter.QuestionAnswerCancelled += OnQuestionAnswerCancelled;
    }

    public event Action<string>? TranslationFailed;

    /// <summary>
    /// Translates text the user explicitly copied. This is the safe fallback for
    /// custom-rendered applications such as WeChat that expose no selectable text
    /// through UI Automation and ignore WM_COPY.
    /// </summary>
    public void TranslateText(
        string? text,
        ScreenPoint anchorPoint,
        TranslationTrigger trigger = TranslationTrigger.Clipboard)
    {
        var normalizedText = TextNormalizer.Normalize(text);
        if (_disposed || string.IsNullOrWhiteSpace(normalizedText))
        {
            return;
        }

        _dispatcher.BeginInvoke(
            () =>
            {
                if (_disposed)
                {
                    return;
                }

                var lease = _requestGate.BeginRequest();
                TrackSelection(lease.Version);
                _ = ProcessExplicitTextAsync(normalizedText, anchorPoint, _getSettings(), lease, trigger);
            },
            DispatcherPriority.Send);
    }

    private void OnSelectionGestureCompleted(SelectionGesture gesture)
    {
        _dispatcher.BeginInvoke(
            () => BeginSelectionIfExternal(gesture),
            DispatcherPriority.Send);
    }

    private void OnMousePressed(ScreenPoint point)
    {
        _dispatcher.BeginInvoke(
            () =>
            {
                if (!_disposed)
                {
                    _popupPresenter.HideTransientPopupIfOutside(point);
                }
            },
            DispatcherPriority.Send);
    }

    private void BeginSelectionIfExternal(SelectionGesture gesture)
    {
        if (_disposed
            || _popupPresenter.IsPointOverPopup(gesture.Start)
            || WindowProcessResolver.IsCurrentProcessAt(gesture.Start)
            || _popupPresenter.IsPointOverPopup(gesture.End)
            || WindowProcessResolver.IsCurrentProcessAt(gesture.End)
            || !WindowProcessResolver.ArePointsInSameExternalWindow(gesture.Start, gesture.End))
        {
            return;
        }

        var settings = _getSettings();
        if (!settings.IsEnabled)
        {
            return;
        }

        var lease = _requestGate.BeginRequest();
        TrackSelection(lease.Version);
        _ = ProcessSelectionAsync(gesture, settings, lease);
    }

    private async Task ProcessSelectionAsync(
        SelectionGesture gesture,
        AppSettings settings,
        RequestLease lease)
    {
        var performance = _performanceMonitor.Begin(
            TranslationTrigger.Selection,
            settings.ProviderId);
        var outcome = TranslationOutcome.Cancelled;
        try
        {
            if (settings.SelectionDelayMilliseconds > 0)
            {
                await Task.Delay(settings.SelectionDelayMilliseconds, lease.CancellationToken).ConfigureAwait(false);
            }

            var selectionReadStartedAt = Stopwatch.GetTimestamp();
            SelectionCapture? capture;
            using var selectionReadCancellation = CancellationTokenSource.CreateLinkedTokenSource(lease.CancellationToken);
            selectionReadCancellation.CancelAfter(SelectionReadTimeout);
            try
            {
                capture = _selectionReader is IContextualSelectionReader contextualReader
                    ? await contextualReader
                        .TryReadSelectionAsync(
                            gesture.End,
                            settings.UseSelectionContext,
                            selectionReadCancellation.Token)
                        .WaitAsync(SelectionReadTimeout, lease.CancellationToken)
                        .ConfigureAwait(false)
                    : await ReadPlainSelectionAsync(
                            _selectionReader,
                            gesture.End,
                            selectionReadCancellation.Token)
                        .WaitAsync(SelectionReadTimeout, lease.CancellationToken)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (selectionReadCancellation.IsCancellationRequested
                                                       && !lease.CancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Selection read budget expired.");
            }
            finally
            {
                // End the actual provider operation too, not only the wait for
                // it. Otherwise a timed-out PDF read can hold its PID slot and
                // make later selections in the same browser look permanently dead.
                selectionReadCancellation.Cancel();
                performance.MarkSelectionRead(Stopwatch.GetElapsedTime(selectionReadStartedAt));
            }
            var selectedText = capture?.Text;
            if (!_requestGate.IsCurrent(lease.Version))
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(selectedText))
            {
                outcome = TranslationOutcome.NoSelection;
                return;
            }

            var targetLanguage = LanguageDirectionResolver.ResolveTargetLanguage(selectedText, settings);
            outcome = await TranslateResolvedTextAsync(
                    lease.Version,
                    selectedText,
                    capture?.Context,
                    settings.SourceLanguage,
                    targetLanguage,
                    gesture.PopupAnchor,
                    settings,
                    lease.CancellationToken,
                    () => IsSelectionRequestActive(lease.Version),
                    performance)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException exception)
        {
            outcome = TranslationOutcome.Failed;
            Debug.WriteLine($"Yita selection request ended unexpectedly: {exception}");
            await FailPopupIfCurrentAsync(lease.Version, Localize(settings, "The translation ended unexpectedly. Try again.", "翻译请求意外中断，请重试。"));
        }
        catch (TimeoutException)
        {
            outcome = TranslationOutcome.Failed;
            Debug.WriteLine("Yita: selection read timed out.");
            await FailPopupIfCurrentAsync(lease.Version, Localize(settings, "Reading the selection timed out. Select the text again.", "读取选中文字超时，请重新选择。"));
        }
        catch (TranslationProviderException exception)
        {
            outcome = TranslationOutcome.Failed;
            Debug.WriteLine($"Yita translation provider failed: {exception}");
            if (IsSelectionRequestActive(lease.Version))
            {
                var message = UiLanguageCatalog.LocalizeProviderError(settings.UiLanguage, exception.Message);
                await FailPopupIfCurrentAsync(lease.Version, message);
                TranslationFailed?.Invoke(message);
            }
        }
        catch (Exception exception)
        {
            outcome = TranslationOutcome.Failed;
            Debug.WriteLine($"Yita selection pipeline failed: {exception}");
            await FailPopupIfCurrentAsync(lease.Version, Localize(settings, "Translation failed. Check the network and try again.", "翻译失败，请检查网络后重试。"));
        }
        finally
        {
            performance.Complete(outcome);
            CompleteSelection(lease.Version);
        }
    }

    private async Task ProcessExplicitTextAsync(
        string text,
        ScreenPoint anchorPoint,
        AppSettings settings,
        RequestLease lease,
        TranslationTrigger trigger)
    {
        var performance = _performanceMonitor.Begin(
            trigger,
            settings.ProviderId);
        var outcome = TranslationOutcome.Cancelled;
        try
        {
            var targetLanguage = LanguageDirectionResolver.ResolveTargetLanguage(text, settings);
            outcome = await TranslateResolvedTextAsync(
                    lease.Version,
                    text,
                    context: null,
                    settings.SourceLanguage,
                    targetLanguage,
                    anchorPoint,
                    settings,
                    lease.CancellationToken,
                    () => IsSelectionRequestActive(lease.Version),
                    performance)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
        }
        catch (TranslationProviderException exception)
        {
            outcome = TranslationOutcome.Failed;
            Debug.WriteLine($"Yita explicit translation failed: {exception}");
            if (IsSelectionRequestActive(lease.Version))
            {
                var message = UiLanguageCatalog.LocalizeProviderError(settings.UiLanguage, exception.Message);
                await FailPopupIfCurrentAsync(lease.Version, message);
                TranslationFailed?.Invoke(message);
            }
        }
        catch (Exception exception)
        {
            outcome = TranslationOutcome.Failed;
            Debug.WriteLine($"Yita explicit translation pipeline failed: {exception}");
            await FailPopupIfCurrentAsync(lease.Version, Localize(settings, "Translation failed. Check the network and try again.", "翻译失败，请检查网络后重试。"));
        }
        finally
        {
            performance.Complete(outcome);
            CompleteSelection(lease.Version);
        }
    }

    private void OnRetranslateRequested(PopupRetranslateRequest request)
    {
        if (_disposed || string.IsNullOrWhiteSpace(request.SourceText))
        {
            return;
        }

        CancelExplanation(request.RequestId);
        CancelSelectionRequest(request.RequestId);

        var cancellation = new CancellationTokenSource();
        lock (_retranslationSync)
        {
            if (_retranslations.Remove(request.RequestId, out var previous))
            {
                previous.Cancel();
            }

            _retranslations[request.RequestId] = cancellation;
        }

        _ = ProcessRetranslationAsync(request, _getSettings(), cancellation);
    }

    private async Task ProcessRetranslationAsync(
        PopupRetranslateRequest request,
        AppSettings settings,
        CancellationTokenSource cancellation)
    {
        var performance = _performanceMonitor.Begin(
            TranslationTrigger.Retranslation,
            settings.ProviderId);
        var outcome = TranslationOutcome.Cancelled;
        try
        {
            outcome = await TranslateResolvedTextAsync(
                    request.RequestId,
                    request.SourceText,
                    context: null,
                    request.SourceLanguage,
                    request.TargetLanguage,
                    request.AnchorPoint,
                    settings,
                    cancellation.Token,
                    () => IsCurrentRetranslation(request.RequestId, cancellation),
                    performance)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException exception)
        {
            outcome = TranslationOutcome.Failed;
            Debug.WriteLine($"Yita retranslation ended unexpectedly: {exception}");
            if (IsCurrentRetranslation(request.RequestId, cancellation))
            {
                await _dispatcher.InvokeAsync(
                    () => _popupPresenter.FailRequest(request.RequestId, Localize(settings, "The translation ended unexpectedly. Try again.", "翻译请求意外中断，请重试。")),
                    DispatcherPriority.Send);
            }
        }
        catch (TranslationProviderException exception)
        {
            outcome = TranslationOutcome.Failed;
            Debug.WriteLine($"Yita retranslation failed: {exception}");
            if (IsCurrentRetranslation(request.RequestId, cancellation))
            {
                var message = UiLanguageCatalog.LocalizeProviderError(settings.UiLanguage, exception.Message);
                await _dispatcher.InvokeAsync(
                    () => _popupPresenter.FailRequest(request.RequestId, message),
                    DispatcherPriority.Send);
                TranslationFailed?.Invoke(message);
            }
        }
        catch (Exception exception)
        {
            outcome = TranslationOutcome.Failed;
            Debug.WriteLine($"Yita retranslation pipeline failed: {exception}");
            if (IsCurrentRetranslation(request.RequestId, cancellation))
            {
                await _dispatcher.InvokeAsync(
                    () => _popupPresenter.FailRequest(request.RequestId, Localize(settings, "Translation failed. Check the network and try again.", "翻译失败，请检查网络后重试。")),
                    DispatcherPriority.Send);
            }
        }
        finally
        {
            performance.Complete(outcome);
            lock (_retranslationSync)
            {
                if (_retranslations.TryGetValue(request.RequestId, out var current)
                    && ReferenceEquals(current, cancellation))
                {
                    _retranslations.Remove(request.RequestId);
                }
            }

            cancellation.Dispose();
        }
    }

    private void OnExplanationRequested(PopupExplanationRequest request)
    {
        if (_disposed
            || string.IsNullOrWhiteSpace(request.SubjectText)
            || string.IsNullOrWhiteSpace(request.SourceText)
            || string.IsNullOrWhiteSpace(request.TranslationText))
        {
            return;
        }

        ExplanationRequestLease operation;
        try
        {
            operation = _explanationGate.Begin(request.RequestId);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _ = ProcessExplanationAsync(request, _getSettings(), operation);
    }

    private void OnExplanationDismissed(long requestId)
    {
        CancelExplanation(requestId);
    }

    private async Task ProcessExplanationAsync(
        PopupExplanationRequest request,
        AppSettings settings,
        ExplanationRequestLease operation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Cancellation.Token);
        timeout.CancelAfter(ExplanationTimeout);
        var enteredConcurrencySlot = false;
        var explanation = new StringBuilder();
        var updateThrottle = new StreamingUpdateThrottle();

        try
        {
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (IsCurrentExplanation(request.RequestId, operation))
                    {
                        _popupPresenter.ShowExplanationLoading(request.RequestId);
                    }
                },
                DispatcherPriority.Send,
                timeout.Token);

            await _explanationConcurrency.WaitAsync(timeout.Token).ConfigureAwait(false);
            enteredConcurrencySlot = true;
            if (!IsCurrentExplanation(request.RequestId, operation))
            {
                return;
            }

            var provider = _translationProviderFactory.CreateExplanationProvider(settings);
            var providerRequest = new ExplanationRequest(
                request.SubjectText,
                request.SourceText,
                request.TranslationText,
                request.SourceLanguage,
                request.TargetLanguage,
                request.Scope);
            await using var enumerator = new SafeAsyncEnumerator<TranslationChunk>(
                provider.ExplainAsync(providerRequest, timeout.Token).GetAsyncEnumerator(timeout.Token),
                () => _explanationConcurrency.Release());
            enteredConcurrencySlot = false; // The iterator now owns slot release, including late cleanup.
            var receivedContent = false;
            while (await MoveNextWithExplanationStageTimeoutAsync(
                       enumerator,
                       receivedContent,
                       timeout).ConfigureAwait(false))
            {
                if (!IsCurrentExplanation(request.RequestId, operation))
                {
                    return;
                }

                var chunk = enumerator.Current;
                explanation.Append(chunk.TextDelta);
                if (!string.IsNullOrEmpty(chunk.TextDelta))
                {
                    receivedContent = true;
                }

                if (!updateThrottle.ShouldPublish(explanation.Length, chunk.IsFinal))
                {
                    continue;
                }

                await ShowExplanationIfCurrentAsync(
                    request.RequestId,
                    operation,
                    explanation.ToString(),
                    timeout.Token).ConfigureAwait(false);
            }

            if (updateThrottle.HasPendingUpdate(explanation.Length))
            {
                await ShowExplanationIfCurrentAsync(
                    request.RequestId,
                    operation,
                    explanation.ToString(),
                    timeout.Token).ConfigureAwait(false);
            }

            if (explanation.Length == 0)
            {
                throw new TranslationProviderException(
                    request.Scope == ExplanationScope.CodeAnalysis
                        ? "DeepSeek 未返回可用的代码分析内容。"
                        : "DeepSeek 未返回可用的解释内容。",
                    TranslationFailureKind.Server);
            }

            if (IsCurrentExplanation(request.RequestId, operation))
            {
                await _dispatcher.InvokeAsync(
                    () =>
                    {
                        if (IsCurrentExplanation(request.RequestId, operation))
                        {
                            _popupPresenter.CompleteExplanation(request.RequestId);
                        }
                    },
                    DispatcherPriority.Normal,
                    timeout.Token);

                try
                {
                    var historyResult = await _aiHistoryStore.AppendExplanationAsync(
                            settings,
                            new AiHistoryContext(
                                request.HistorySessionId,
                                request.HistoryCreatedAt,
                                request.SourceText,
                                request.TranslationText,
                                request.SourceLanguage,
                                request.TargetLanguage),
                            request.SubjectText,
                            request.Scope,
                            HighlightMarkup.ToPlainText(explanation.ToString()),
                            DateTimeOffset.Now,
                            timeout.Token)
                        .ConfigureAwait(false);
                    if (historyResult.ErrorCode is not null)
                    {
                        Debug.WriteLine($"Yita AI history explanation save skipped: {historyResult.ErrorCode}");
                    }
                }
                catch (Exception exception) when (exception is OperationCanceledException
                                                  or IOException
                                                  or UnauthorizedAccessException
                                                  or ArgumentException
                                                  or NotSupportedException)
                {
                    // Archiving is optional. A disk or cancellation failure must
                    // never turn a successfully displayed explanation into an error.
                    Debug.WriteLine($"Yita AI history explanation save skipped: {exception.GetType().Name}");
                }
            }
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            // The user left the explanation, the popup closed, or a newer
            // explanation replaced this one. Never surface stale failures.
        }
        catch (TranslationProviderException exception)
        {
            Debug.WriteLine($"Yita explanation provider failed: {exception}");
            await FailExplanationIfCurrentAsync(
                request.RequestId,
                operation,
                UiLanguageCatalog.LocalizeProviderError(settings.UiLanguage, exception.Message)).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            Debug.WriteLine($"Yita explanation ended unexpectedly: {exception}");
            var message = timeout.IsCancellationRequested
                ? request.Scope == ExplanationScope.CodeAnalysis
                    ? Localize(settings, "Code analysis timed out. Try again.", "代码分析超时，请重试。")
                    : Localize(settings, "AI explanation timed out. Try again.", "AI 解释超时，请重试。")
                : request.Scope == ExplanationScope.CodeAnalysis
                    ? Localize(settings, "Code analysis ended unexpectedly. Try again.", "代码分析意外中断，请重试。")
                    : Localize(settings, "AI explanation ended unexpectedly. Try again.", "AI 解释意外中断，请重试。");
            await FailExplanationIfCurrentAsync(request.RequestId, operation, message).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Yita explanation pipeline failed: {exception}");
            await FailExplanationIfCurrentAsync(
                request.RequestId,
                operation,
                request.Scope == ExplanationScope.CodeAnalysis
                    ? Localize(settings, "Code analysis failed. Check the network and try again.", "代码分析失败，请检查网络后重试。")
                    : Localize(settings, "AI explanation failed. Check the network and try again.", "AI 解释失败，请检查网络后重试。")).ConfigureAwait(false);
        }
        finally
        {
            if (enteredConcurrencySlot)
            {
                _explanationConcurrency.Release();
            }

            _explanationGate.Complete(operation);
            operation.Cancellation.Dispose();
        }
    }

    private async Task ShowExplanationIfCurrentAsync(
        long requestId,
        ExplanationRequestLease operation,
        string explanation,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentExplanation(requestId, operation))
        {
            return;
        }

        await _dispatcher.InvokeAsync(
            () =>
            {
                if (IsCurrentExplanation(requestId, operation))
                {
                    _popupPresenter.ShowExplanation(requestId, explanation);
                }
            },
            DispatcherPriority.Normal,
            cancellationToken);
    }

    private async Task FailExplanationIfCurrentAsync(
        long requestId,
        ExplanationRequestLease operation,
        string message)
    {
        if (!IsCurrentExplanation(requestId, operation))
        {
            return;
        }

        await _dispatcher.InvokeAsync(
            () =>
            {
                if (IsCurrentExplanation(requestId, operation))
                {
                    _popupPresenter.FailExplanation(requestId, message);
                }
            },
            DispatcherPriority.Send);
    }

    private async Task<TranslationOutcome> TranslateResolvedTextAsync(
        long requestId,
        string sourceText,
        string? context,
        string sourceLanguage,
        string targetLanguage,
        ScreenPoint anchorPoint,
        AppSettings settings,
        CancellationToken cancellationToken,
        Func<bool> canPresent,
        TranslationPerformanceOperation performance)
    {
        if (sourceText.Length > settings.MaximumSelectionCharacters)
        {
            await _dispatcher.InvokeAsync(
                () =>
                {
                    _popupPresenter.ShowLoading(requestId, anchorPoint);
                    _popupPresenter.FailRequest(
                        requestId,
                        Localize(
                            settings,
                            $"The selection is too long ({sourceText.Length} characters). The limit is {settings.MaximumSelectionCharacters}; select less text.",
                            $"选中文字过长（{sourceText.Length} 字符），上限为 {settings.MaximumSelectionCharacters}。请缩小选区。"));
                },
                DispatcherPriority.Send,
                cancellationToken);
            return TranslationOutcome.Failed;
        }

        performance.MarkTranslationStarted();
        var savedTranslation = _translationMemoryStore.FindExact(
            sourceText,
            sourceLanguage,
            targetLanguage);
        if (savedTranslation is not null
            && !TranslationOutputGuard.IsInstructionEcho(sourceText, savedTranslation.TargetText))
        {
            performance.MarkCacheHit();
            performance.MarkFirstContent();
            if (!canPresent())
            {
                return TranslationOutcome.Cancelled;
            }

            await _dispatcher.InvokeAsync(
                () =>
                {
                    _popupPresenter.ShowTranslation(
                        requestId,
                        sourceText,
                        savedTranslation.TargetText,
                        targetLanguage,
                        anchorPoint,
                        sourceLanguage);
                    _popupPresenter.CompleteRequest(requestId);
                },
                DispatcherPriority.Send,
                cancellationToken);
            return TranslationOutcome.Succeeded;
        }

        var preparedOptions = PrepareTranslationOptions(
            sourceText,
            sourceLanguage,
            targetLanguage,
            settings);
        var cacheKey = CreateCacheKey(
            sourceText,
            context,
            sourceLanguage,
            targetLanguage,
            settings,
            preparedOptions);
        if (_translationCache.TryGet(cacheKey, out var cachedTranslation)
            && !TranslationOutputGuard.IsInstructionEcho(sourceText, cachedTranslation))
        {
            performance.MarkCacheHit();
            performance.MarkFirstContent();
            if (!canPresent())
            {
                return TranslationOutcome.Cancelled;
            }

            await _dispatcher.InvokeAsync(
                () =>
                {
                    _popupPresenter.ShowTranslation(
                        requestId,
                        sourceText,
                        cachedTranslation,
                        targetLanguage,
                        anchorPoint,
                        sourceLanguage);
                    _popupPresenter.CompleteRequest(requestId);
                },
                DispatcherPriority.Send,
                cancellationToken);
            return TranslationOutcome.Succeeded;
        }

        while (_inFlightTranslations.TryJoin(cacheKey, out var pendingTranslation)
               && pendingTranslation is not null)
        {
            performance.MarkCoalesced();
            await _dispatcher.InvokeAsync(
                () => _popupPresenter.ShowLoading(requestId, anchorPoint),
                DispatcherPriority.Send,
                cancellationToken);
            string sharedTranslation;
            try
            {
                sharedTranslation = await pendingTranslation
                    .WaitAsync(TranslationTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A failed owner must not strand every identical request. Drop
                // the stale registry entry and let this caller take ownership.
                _inFlightTranslations.TryRemove(cacheKey, pendingTranslation);
                continue;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _inFlightTranslations.TryRemove(cacheKey, pendingTranslation);
                continue;
            }

            if (!canPresent())
            {
                return TranslationOutcome.Cancelled;
            }

            performance.MarkFirstContent();
            await _dispatcher.InvokeAsync(
                () =>
                {
                    _popupPresenter.ShowTranslation(
                        requestId,
                        sourceText,
                        sharedTranslation,
                        targetLanguage,
                        anchorPoint,
                        sourceLanguage);
                    _popupPresenter.CompleteRequest(requestId);
                },
                DispatcherPriority.Send,
                cancellationToken);
            return TranslationOutcome.Succeeded;
        }

        var sharedCompletion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = sharedCompletion.Task.ContinueWith(
            task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        if (!_inFlightTranslations.TryRegister(cacheKey, sharedCompletion.Task))
        {
            return await TranslateResolvedTextAsync(
                    requestId,
                    sourceText,
                    context,
                    sourceLanguage,
                    targetLanguage,
                    anchorPoint,
                    settings,
                    cancellationToken,
                    canPresent,
                    performance)
                .ConfigureAwait(false);
        }

        try
        {
            // Do not create a popup until a real selection has been read. Cache hits
            // skip loading entirely; network requests retain the immediate feedback.
            await _dispatcher.InvokeAsync(
                () => _popupPresenter.ShowLoading(requestId, anchorPoint),
                DispatcherPriority.Send,
                cancellationToken);

            var translation = await StreamTranslationAsync(
                    requestId,
                    sourceText,
                    context,
                    sourceLanguage,
                    targetLanguage,
                    anchorPoint,
                    settings,
                    preparedOptions,
                    cacheKey,
                    cancellationToken,
                    canPresent,
                    performance)
                .ConfigureAwait(false);
            if (translation is null)
            {
                sharedCompletion.TrySetCanceled(cancellationToken);
                if (cancellationToken.IsCancellationRequested || !canPresent())
                {
                    return TranslationOutcome.Cancelled;
                }

                throw new TranslationProviderException(
                    "DeepSeek 未返回可用的译文内容。",
                    TranslationFailureKind.Server);
            }

            sharedCompletion.TrySetResult(translation);
            await _dispatcher.InvokeAsync(
                () => _popupPresenter.CompleteRequest(requestId),
                DispatcherPriority.Send,
                cancellationToken);
            return TranslationOutcome.Succeeded;
        }
        catch (OperationCanceledException)
        {
            sharedCompletion.TrySetCanceled(cancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            sharedCompletion.TrySetException(exception);
            throw;
        }
        finally
        {
            _inFlightTranslations.TryRemove(cacheKey, sharedCompletion.Task);
        }
    }

    private void OnQuestionAnswerRequested(PopupQuestionAnswerRequest request)
    {
        if (_disposed
            || request.SessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Question)
            || (request.ContextKind != QuestionContextKind.GeneralChat
                && (string.IsNullOrWhiteSpace(request.SourceText)
                    || string.IsNullOrWhiteSpace(request.TranslationText))))
        {
            return;
        }

        QuestionAnswerRequestLease operation;
        try
        {
            operation = _questionAnswerGate.Begin(request.SessionId);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _ = ProcessQuestionAnswerAsync(request, _getSettings(), operation);
    }

    private void OnQuestionAnswerCancelled(Guid sessionId)
    {
        _questionAnswerGate.Cancel(sessionId);
    }

    private async Task ProcessQuestionAnswerAsync(
        PopupQuestionAnswerRequest request,
        AppSettings settings,
        QuestionAnswerRequestLease operation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Cancellation.Token);
        timeout.CancelAfter(QuestionAnswerTimeout);
        var enteredConcurrencySlot = false;
        var answer = new StringBuilder();
        var updateThrottle = new StreamingUpdateThrottle();

        try
        {
            await _questionAnswerConcurrency.WaitAsync(timeout.Token).ConfigureAwait(false);
            enteredConcurrencySlot = true;
            if (!IsCurrentQuestionAnswer(operation))
            {
                return;
            }

            var providerSettings = request.ContextKind == QuestionContextKind.GeneralChat
                ? settings with { ProviderId = "deepseek" }
                : settings;
            var provider = _translationProviderFactory.CreateQuestionAnswerProvider(providerSettings);
            var providerRequest = new QuestionAnswerRequest(
                request.Question,
                request.SourceText,
                request.TranslationText,
                request.ExplanationText,
                request.SourceLanguage,
                request.TargetLanguage,
                request.UiLanguage,
                request.ContextKind,
                request.History);
            await using var enumerator = new SafeAsyncEnumerator<TranslationChunk>(
                provider.AnswerAsync(providerRequest, timeout.Token).GetAsyncEnumerator(timeout.Token),
                () => _questionAnswerConcurrency.Release());
            enteredConcurrencySlot = false;
            var receivedContent = false;
            while (await MoveNextWithQuestionAnswerStageTimeoutAsync(
                       enumerator,
                       receivedContent,
                       timeout).ConfigureAwait(false))
            {
                if (!IsCurrentQuestionAnswer(operation))
                {
                    return;
                }

                var chunk = enumerator.Current;
                answer.Append(chunk.TextDelta);
                if (!string.IsNullOrEmpty(chunk.TextDelta))
                {
                    receivedContent = true;
                }

                if (!updateThrottle.ShouldPublish(answer.Length, chunk.IsFinal))
                {
                    continue;
                }

                await ShowQuestionAnswerIfCurrentAsync(
                    operation,
                    answer.ToString(),
                    timeout.Token).ConfigureAwait(false);
            }

            if (updateThrottle.HasPendingUpdate(answer.Length))
            {
                await ShowQuestionAnswerIfCurrentAsync(
                    operation,
                    answer.ToString(),
                    timeout.Token).ConfigureAwait(false);
            }

            if (answer.Length == 0)
            {
                throw new TranslationProviderException(
                    "DeepSeek 未返回可用的问答内容。",
                    TranslationFailureKind.Server);
            }

            if (!IsCurrentQuestionAnswer(operation))
            {
                return;
            }

            var finalAnswer = answer.ToString();
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (IsCurrentQuestionAnswer(operation))
                    {
                        _popupPresenter.CompleteQuestionAnswer(request.SessionId, finalAnswer);
                    }
                },
                DispatcherPriority.Normal,
                timeout.Token);

            if (request.ContextKind != QuestionContextKind.GeneralChat)
            {
                try
                {
                    var historyResult = await _aiHistoryStore.AppendQuestionAnswerAsync(
                            settings,
                            new AiHistoryContext(
                                request.SessionId,
                                request.HistoryCreatedAt,
                                request.SourceText,
                                request.TranslationText,
                                request.SourceLanguage,
                                request.TargetLanguage),
                            request.Question,
                            HighlightMarkup.ToPlainText(finalAnswer),
                            request.ContextKind,
                            DateTimeOffset.Now,
                            timeout.Token)
                        .ConfigureAwait(false);
                    if (historyResult.ErrorCode is not null)
                    {
                        Debug.WriteLine($"Yita AI history Q&A save skipped: {historyResult.ErrorCode}");
                    }
                }
                catch (Exception exception) when (exception is OperationCanceledException
                                                  or IOException
                                                  or UnauthorizedAccessException
                                                  or ArgumentException
                                                  or NotSupportedException)
                {
                    // Optional history is isolated from the live answer path.
                    Debug.WriteLine($"Yita AI history Q&A save skipped: {exception.GetType().Name}");
                }
            }
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
        }
        catch (TranslationProviderException exception)
        {
            Debug.WriteLine($"Yita question-answer provider failed: {exception}");
            await FailQuestionAnswerIfCurrentAsync(
                request.SessionId,
                operation,
                UiLanguageCatalog.LocalizeProviderError(settings.UiLanguage, exception.Message))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            Debug.WriteLine($"Yita question-answer ended unexpectedly: {exception}");
            await FailQuestionAnswerIfCurrentAsync(
                request.SessionId,
                operation,
                Localize(settings, "AI answer timed out. Try again.", "AI 回答超时，请重试。"))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Yita question-answer pipeline failed: {exception}");
            await FailQuestionAnswerIfCurrentAsync(
                request.SessionId,
                operation,
                Localize(settings, "AI answer failed. Check the network and try again.", "AI 回答失败，请检查网络后重试。"))
                .ConfigureAwait(false);
        }
        finally
        {
            if (enteredConcurrencySlot)
            {
                _questionAnswerConcurrency.Release();
            }

            _questionAnswerGate.Complete(operation);
            operation.Cancellation.Dispose();
        }
    }

    private async Task ShowQuestionAnswerIfCurrentAsync(
        QuestionAnswerRequestLease operation,
        string answer,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentQuestionAnswer(operation))
        {
            return;
        }

        await _dispatcher.InvokeAsync(
            () =>
            {
                if (IsCurrentQuestionAnswer(operation))
                {
                    _popupPresenter.ShowQuestionAnswer(operation.SessionId, answer);
                }
            },
            DispatcherPriority.Normal,
            cancellationToken);
    }

    private async Task FailQuestionAnswerIfCurrentAsync(
        Guid sessionId,
        QuestionAnswerRequestLease operation,
        string message)
    {
        if (!IsCurrentQuestionAnswer(operation))
        {
            return;
        }

        await _dispatcher.InvokeAsync(
            () =>
            {
                if (IsCurrentQuestionAnswer(operation))
                {
                    _popupPresenter.FailQuestionAnswer(sessionId, message);
                }
            },
            DispatcherPriority.Send);
    }

    private async Task<string?> StreamTranslationAsync(
        long requestId,
        string sourceText,
        string? context,
        string sourceLanguage,
        string targetLanguage,
        ScreenPoint anchorPoint,
        AppSettings settings,
        PreparedTranslationOptions preparedOptions,
        TranslationCacheKey cacheKey,
        CancellationToken cancellationToken,
        Func<bool> canPresent,
        TranslationPerformanceOperation performance)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TranslationTimeout);
        var request = new TranslationRequest(
            sourceText,
            sourceLanguage,
            targetLanguage,
            context,
            preparedOptions.Mode,
            preparedOptions.Tone,
            PersonalGlossary: string.Empty,
            ApplicableGlossaryEntries: preparedOptions.ApplicableGlossaryEntries,
            TranslationExamples: preparedOptions.TranslationExamples);
        var translationProvider = _translationProviderFactory.Create(settings);
        var translation = new StringBuilder();
        var updateThrottle = new StreamingUpdateThrottle();
        var enteredConcurrencySlot = false;

        try
        {
            performance.MarkQueueStarted();
            await _translationConcurrency.WaitAsync(timeout.Token).ConfigureAwait(false);
            enteredConcurrencySlot = true;
            performance.MarkQueueCompleted();
            await using var enumerator = new SafeAsyncEnumerator<TranslationChunk>(
                translationProvider.TranslateAsync(request, timeout.Token).GetAsyncEnumerator(timeout.Token),
                () => _translationConcurrency.Release());
            enteredConcurrencySlot = false;
            var receivedContent = false;
            while (await MoveNextWithStageTimeoutAsync(
                       enumerator,
                       receivedContent,
                       timeout).ConfigureAwait(false))
            {
                var chunk = enumerator.Current;
                if (!canPresent())
                {
                    return null;
                }

                translation.Append(chunk.TextDelta);
                if (!receivedContent && chunk.TextDelta.Length > 0)
                {
                    receivedContent = true;
                    performance.MarkFirstContent();
                }
                if (!updateThrottle.ShouldPublish(translation.Length, chunk.IsFinal))
                {
                    continue;
                }

                var currentText = translation.ToString();
                await _dispatcher.InvokeAsync(
                    () => _popupPresenter.ShowTranslation(
                        requestId,
                        sourceText,
                        currentText,
                        targetLanguage,
                        anchorPoint,
                        sourceLanguage),
                    DispatcherPriority.Normal,
                    timeout.Token);
            }

            if (updateThrottle.HasPendingUpdate(translation.Length) && canPresent())
            {
                var finalText = translation.ToString();
                await _dispatcher.InvokeAsync(
                    () => _popupPresenter.ShowTranslation(
                        requestId,
                        sourceText,
                        finalText,
                        targetLanguage,
                        anchorPoint,
                        sourceLanguage),
                    DispatcherPriority.Normal,
                    timeout.Token);
            }

            if (translation.Length > 0)
            {
                var completedTranslation = translation.ToString();
                if (!string.IsNullOrWhiteSpace(HighlightMarkup.ToPlainText(completedTranslation)))
                {
                    // The in-memory cache keeps display metadata so cache hits
                    // retain their emphasis. Popup state, copy, history, and
                    // saved translation memory always use the parsed plain text.
                    _translationCache.Set(cacheKey, completedTranslation);
                    return completedTranslation;
                }
            }

            return null;
        }
        catch (OperationCanceledException)
            when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TranslationProviderException(
                "DeepSeek 翻译请求超过 60 秒，已取消。",
                TranslationFailureKind.Timeout);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationProviderException(
                "DeepSeek 翻译请求意外中断，请重试。",
                exception,
                TranslationFailureKind.Connectivity);
        }
        finally
        {
            if (enteredConcurrencySlot)
            {
                _translationConcurrency.Release();
            }
        }
    }

    private static async Task<bool> MoveNextWithStageTimeoutAsync(
        IAsyncEnumerator<TranslationChunk> enumerator,
        bool receivedContent,
        CancellationTokenSource requestTimeout)
    {
        var stageTimeout = receivedContent ? StreamIdleTimeout : FirstContentTimeout;
        try
        {
            return await enumerator.MoveNextAsync()
                .AsTask()
                .WaitAsync(stageTimeout, requestTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            requestTimeout.Cancel();
            throw new TranslationProviderException(
                receivedContent
                    ? "DeepSeek 流式响应停顿超过 15 秒，已取消。"
                    : "DeepSeek 在 20 秒内未返回首段译文，请检查网络后重试。",
                exception,
                TranslationFailureKind.Timeout);
        }
    }

    private static async Task<bool> MoveNextWithExplanationStageTimeoutAsync(
        IAsyncEnumerator<TranslationChunk> enumerator,
        bool receivedContent,
        CancellationTokenSource requestTimeout)
    {
        var stageTimeout = receivedContent
            ? ExplanationStreamIdleTimeout
            : ExplanationFirstContentTimeout;
        try
        {
            return await enumerator.MoveNextAsync()
                .AsTask()
                .WaitAsync(stageTimeout, requestTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            requestTimeout.Cancel();
            throw new TranslationProviderException(
                receivedContent
                    ? "DeepSeek 解释流停顿超过 15 秒，已取消。"
                    : "DeepSeek 在 15 秒内未返回首段解释，请检查网络后重试。",
                exception,
                TranslationFailureKind.Timeout);
        }
    }

    private static async Task<bool> MoveNextWithQuestionAnswerStageTimeoutAsync(
        IAsyncEnumerator<TranslationChunk> enumerator,
        bool receivedContent,
        CancellationTokenSource requestTimeout)
    {
        var stageTimeout = receivedContent
            ? QuestionAnswerStreamIdleTimeout
            : QuestionAnswerFirstContentTimeout;
        try
        {
            return await enumerator.MoveNextAsync()
                .AsTask()
                .WaitAsync(stageTimeout, requestTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            requestTimeout.Cancel();
            throw new TranslationProviderException(
                receivedContent
                    ? "DeepSeek 问答流停顿超过 15 秒，已取消。"
                    : "DeepSeek 在 15 秒内未返回首段回答，请检查网络后重试。",
                exception,
                TranslationFailureKind.Timeout);
        }
    }

    private static TranslationCacheKey CreateCacheKey(
        string sourceText,
        string? context,
        string sourceLanguage,
        string targetLanguage,
        AppSettings settings,
        PreparedTranslationOptions preparedOptions)
    {
        var applicableGlossary = string.Join(
            '\n',
            preparedOptions.ApplicableGlossaryEntries
                .Select(entry => $"{entry.Source}=>{entry.Target}"));
        var optionsSignature = string.Join(
            '\u001F',
            context ?? string.Empty,
            preparedOptions.Mode,
            preparedOptions.Tone,
            applicableGlossary,
            string.Join(
                '\u001E',
                preparedOptions.TranslationExamples.Select(example =>
                    $"{example.SourceText}\u001D{example.TargetText}")));
        return TranslationCacheKey.Create(
            settings.ProviderId,
            settings.DeepSeekEndpoint,
            settings.DeepSeekModel,
            sourceLanguage,
            targetLanguage,
            sourceText,
            PromptVersion,
            optionsSignature);
    }

    private PreparedTranslationOptions PrepareTranslationOptions(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        AppSettings settings)
    {
        var applicableGlossaryEntries = PersonalGlossary.Parse(settings.PersonalGlossary)
            .Where(entry => sourceText.Contains(entry.Source, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var translationExamples = _translationMemoryStore
            .FindRelevant(
                sourceText,
                sourceLanguage,
                targetLanguage)
            .Select(entry => new TranslationExample(entry.SourceText, entry.TargetText))
            .ToArray();
        return new PreparedTranslationOptions(
            TranslationPreferenceCatalog.NormalizeMode(settings.TranslationMode),
            TranslationPreferenceCatalog.NormalizeTone(settings.TranslationTone),
            applicableGlossaryEntries,
            translationExamples);
    }

    private static async Task<SelectionCapture?> ReadPlainSelectionAsync(
        ISelectionReader reader,
        ScreenPoint point,
        CancellationToken cancellationToken)
    {
        var text = await reader.TryReadSelectedTextAsync(point, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : new SelectionCapture(text);
    }

    private static string Localize(AppSettings settings, string english, string chinese)
    {
        return settings.UiLanguage == UiLanguageCatalog.SimplifiedChineseLanguageId
            ? chinese
            : english;
    }

    private sealed record PreparedTranslationOptions(
        string Mode,
        string Tone,
        IReadOnlyList<GlossaryEntry> ApplicableGlossaryEntries,
        IReadOnlyList<TranslationExample> TranslationExamples);

    private void OnPopupClosed(long requestId)
    {
        CancelExplanation(requestId);
        CancelSelectionRequest(requestId);
        lock (_retranslationSync)
        {
            if (_retranslations.Remove(requestId, out var cancellation))
            {
                cancellation.Cancel();
            }
        }
    }

    private bool OnTranslationCorrectionRequested(PopupTranslationCorrection correction)
    {
        try
        {
            var settings = _getSettings();
            _translationMemoryStore.AddOrUpdate(
                correction.SourceText,
                correction.CorrectedTranslation,
                correction.SourceLanguage,
                correction.TargetLanguage);
            _translationCache.Clear();
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException
            or ArgumentException)
        {
            Debug.WriteLine($"Yita could not save a translation correction: {exception}");
            TranslationFailed?.Invoke(Localize(
                _getSettings(),
                "Could not save the correction securely.",
                "无法安全保存修正译文。"));
            return false;
        }
    }

    private void OnPopupPinStateChanged(long requestId, bool isPinned)
    {
        if (!isPinned || !_requestGate.TryDetach(requestId, out var cancellation) || cancellation is null)
        {
            return;
        }

        lock (_retranslationSync)
        {
            if (_runningSelections.Contains(requestId))
            {
                _detachedSelections[requestId] = cancellation;
            }
            else
            {
                cancellation.Dispose();
            }
        }
    }

    private bool IsSelectionRequestActive(long requestId)
    {
        if (_requestGate.IsCurrent(requestId))
        {
            return true;
        }

        lock (_retranslationSync)
        {
            return !_disposed
                && _detachedSelections.TryGetValue(requestId, out var cancellation)
                && !cancellation.IsCancellationRequested;
        }
    }

    private void CancelSelectionRequest(long requestId)
    {
        _requestGate.CancelIfCurrent(requestId);
        lock (_retranslationSync)
        {
            if (_detachedSelections.TryGetValue(requestId, out var cancellation))
            {
                cancellation.Cancel();
                if (!_runningSelections.Contains(requestId))
                {
                    _detachedSelections.Remove(requestId);
                    cancellation.Dispose();
                }
            }
        }
    }

    private void TrackSelection(long requestId)
    {
        lock (_retranslationSync)
        {
            _runningSelections.Add(requestId);
        }
    }

    private void CompleteSelection(long requestId)
    {
        lock (_retranslationSync)
        {
            _runningSelections.Remove(requestId);
            if (_detachedSelections.Remove(requestId, out var cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private bool IsCurrentRetranslation(long requestId, CancellationTokenSource cancellation)
    {
        lock (_retranslationSync)
        {
            return !_disposed
                   && _retranslations.TryGetValue(requestId, out var current)
                   && ReferenceEquals(current, cancellation)
                   && !cancellation.IsCancellationRequested;
        }
    }

    private bool IsCurrentExplanation(long requestId, ExplanationRequestLease operation)
    {
        return !_disposed
               && operation.RequestId == requestId
               && _explanationGate.IsCurrent(operation);
    }

    private bool IsCurrentQuestionAnswer(QuestionAnswerRequestLease operation)
    {
        return !_disposed && _questionAnswerGate.IsCurrent(operation);
    }

    private void CancelExplanation(long requestId)
    {
        _explanationGate.Cancel(requestId);
    }

    private async Task FailPopupIfCurrentAsync(long version, string? message = null)
    {
        if (!IsSelectionRequestActive(version))
        {
            return;
        }

        await _dispatcher.InvokeAsync(
            () =>
            {
                if (IsSelectionRequestActive(version))
                {
                    _popupPresenter.FailRequest(version, message);
                }
            },
            DispatcherPriority.Send);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mouseHook.MousePressed -= OnMousePressed;
        _mouseHook.SelectionGestureCompleted -= OnSelectionGestureCompleted;
        _popupPresenter.RetranslateRequested -= OnRetranslateRequested;
        _popupPresenter.PopupClosed -= OnPopupClosed;
        _popupPresenter.PinStateChanged -= OnPopupPinStateChanged;
        _popupPresenter.TranslationCorrectionRequested -= OnTranslationCorrectionRequested;
        _popupPresenter.ExplanationRequested -= OnExplanationRequested;
        _popupPresenter.ExplanationDismissed -= OnExplanationDismissed;
        _popupPresenter.QuestionAnswerRequested -= OnQuestionAnswerRequested;
        _popupPresenter.QuestionAnswerCancelled -= OnQuestionAnswerCancelled;
        _requestGate.Dispose();
        _translationCache.Clear();

        lock (_retranslationSync)
        {
            foreach (var cancellation in _retranslations.Values)
            {
                cancellation.Cancel();
            }

            _retranslations.Clear();

            foreach (var (requestId, cancellation) in _detachedSelections.ToArray())
            {
                cancellation.Cancel();
                if (!_runningSelections.Contains(requestId))
                {
                    _detachedSelections.Remove(requestId);
                    cancellation.Dispose();
                }
            }
        }

        _explanationGate.Dispose();
        _questionAnswerGate.Dispose();
    }
}
