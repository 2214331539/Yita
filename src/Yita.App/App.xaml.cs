using System.Reflection;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Yita.Hooks;
using Yita.Interop;
using Yita.Models;
using Yita.Selection;
using Yita.Services;
using Yita.Settings;
using Yita.Translation;
using Yita.Windows;
using WpfMessageBox = System.Windows.MessageBox;
using WpfClipboard = System.Windows.Clipboard;
using WpfTextDataFormat = System.Windows.TextDataFormat;

namespace Yita;

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan PeriodicMouseHookRebindInterval = TimeSpan.FromMinutes(15);
    private readonly SettingsStore _settingsStore = new(new WindowsCredentialStore());
    private readonly StartupRegistration _startupRegistration = new();
    private readonly TranslationPerformanceMonitor _performanceMonitor = new();
    private readonly TranslationMemoryStore _translationMemoryStore = new();
    private readonly AiHistoryStore _aiHistoryStore = new();
    private readonly RuntimeHealthJournal _healthJournal = new();
    private volatile AppSettings _settings = AppSettings.Default;
    private GlobalMouseHook? _mouseHook;
    private IsolatedUiaSelectionReader? _uiaSelectionReader;
    private GlobalHotkeyManager? _hotkeyManager;
    private SelectionTranslationCoordinator? _coordinator;
    private ZoteroSelectionBridge? _zoteroSelectionBridge;
    private TranslationProviderFactory? _translationProviderFactory;
    private AiSummaryService? _aiSummaryService;
    private PopupManager? _popupManager;
    private TrayIconManager? _trayIcon;
    private SingleInstanceCoordinator? _singleInstance;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _smokeTestTimer;
    private DispatcherTimer? _mouseHookRecoveryTimer;
    private DispatcherTimer? _credentialRecoveryTimer;
    private bool _isExiting;
    private bool _isMouseHookRecoveryInProgress;
    private bool _isCredentialRecoveryInProgress;
    private int _mouseHookRecoveryFailures;
    private int _credentialRecoveryFailures;
    private int _settingsRevision;
    private bool _isSystemParametersSubscribed;
    private bool _isSessionEventsSubscribed;
    private bool _isRuntimeDiagnosticsSubscribed;
    private int _exitCode;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SubscribeRuntimeDiagnostics();
        _healthJournal.Record(RuntimeHealthEvent.AppStarted);

        var isPopupSmokeTest = e.Args.Contains("--popup-smoke-test", StringComparer.OrdinalIgnoreCase);
        var isSmokeTest = e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        var isPopupSnapshotTest = e.Args.Contains("--popup-snapshot-test", StringComparer.OrdinalIgnoreCase);
        var isSettingsSnapshotTest = e.Args.Contains("--settings-snapshot-test", StringComparer.OrdinalIgnoreCase);
        var isSettingsLifecycleTest = e.Args.Contains("--settings-lifecycle-test", StringComparer.OrdinalIgnoreCase);
        var isAiSmokeTest = e.Args.Contains("--ai-smoke-test", StringComparer.OrdinalIgnoreCase);
        var isTranslationQualityTest = e.Args.Contains("--translation-quality-smoke-test", StringComparer.OrdinalIgnoreCase);
        var isVisualTest = isPopupSmokeTest
                           || isSmokeTest
                           || isPopupSnapshotTest
                           || isSettingsSnapshotTest
                           || isSettingsLifecycleTest
                           || isAiSmokeTest || isTranslationQualityTest;
        if (!isVisualTest)
        {
            _singleInstance = SingleInstanceCoordinator.AcquireWithTakeoverRetry();
            if (!_singleInstance.IsPrimary)
            {
                _healthJournal.Record(
                    _singleInstance.WasActivationAcknowledged
                        ? RuntimeHealthEvent.SecondaryInstanceExited
                        : RuntimeHealthEvent.PrimaryInstanceUnresponsive);
                _singleInstance.Dispose();
                _singleInstance = null;
                Shutdown(0);
                return;
            }
        }

        // Reading Windows Credential Manager can be slow while the interactive
        // desktop is still starting. Load ordinary preferences synchronously,
        // then recover the secret off the UI thread after tray/input are live.
        _settings = isAiSmokeTest || isTranslationQualityTest ? _settingsStore.Load() : _settingsStore.LoadPreferences();
        if (_settingsStore.SettingsReadFailed)
        {
            _healthJournal.Record(RuntimeHealthEvent.SettingsReadFailed);
        }
        ThemeManager.Apply(_settings);
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        _isSystemParametersSubscribed = true;
        if (!isVisualTest)
        {
            TrySubscribeSessionEvents();
        }

        // Visual review modes render only our own WPF windows. They deliberately
        // skip global hooks, hotkeys, tray integration, and startup registration.
        if (isAiSmokeTest || isTranslationQualityTest)
        {
            _translationProviderFactory = new TranslationProviderFactory();
            _ = isTranslationQualityTest ? RunTranslationQualitySmokeTestAsync() : RunAiSmokeTestAsync();
            return;
        }

        if (isPopupSmokeTest || isPopupSnapshotTest)
        {
            _popupManager = new PopupManager(
                () => new PopupAppearanceSettings(
                    16.5,
                    TranslationFontCatalog.DefaultEnglishFontFamily,
                    TranslationFontCatalog.DefaultChineseFontFamily,
                    UiLanguageCatalog.EnglishLanguageId,
                    PopupVisualStyleCatalog.DefaultStyleId));
            _ = isPopupSnapshotTest
                ? RunPopupSnapshotTestAsync()
                : RunPopupSmokeTestAsync();
            return;
        }

        if (isSettingsSnapshotTest)
        {
            _ = RunSettingsSnapshotTestAsync();
            return;
        }

        if (isSettingsLifecycleTest)
        {
            _ = RunSettingsLifecycleTestAsync();
            return;
        }

        try
        {
            _popupManager = CreatePopupManager();
            _mouseHook = new GlobalMouseHook();
            _mouseHook.HookStoppedUnexpectedly += OnMouseHookStoppedUnexpectedly;
            _translationProviderFactory = new TranslationProviderFactory();
            _aiSummaryService = new AiSummaryService(_aiHistoryStore, _translationProviderFactory);
            _uiaSelectionReader = new IsolatedUiaSelectionReader();
            _ = _uiaSelectionReader.WarmUpAsync();
            _coordinator = new SelectionTranslationCoordinator(
                _mouseHook,
                new SelectionReaderPipeline(
                    _uiaSelectionReader,
                    new NativeSelectionReader(),
                    new ClipboardSelectionReader(Dispatcher, WindowProcessResolver.IsClipboardFallbackAllowedAt),
                    () => _settings.UseClipboardFallback,
                    WindowProcessResolver.RequiresSelectionStabilizationAt,
                    allowTargetClipboardFallback: point => (_settings.UseWpsPdfCompatibility
                        && WindowProcessResolver.IsWpsPdfAt(point))
                        || _uiaSelectionReader.RequiresCompatibilityAt(point)),
                _translationProviderFactory,
                _popupManager,
                Dispatcher,
                GetSettingsSnapshot,
                _performanceMonitor,
                _translationMemoryStore,
                _aiHistoryStore);
            _coordinator.TranslationFailed += OnTranslationFailed;

            try
            {
                _zoteroSelectionBridge = new ZoteroSelectionBridge();
                _zoteroSelectionBridge.SelectionReceived += OnZoteroSelectionReceived;
                _zoteroSelectionBridge.Start();
                _healthJournal.Record(RuntimeHealthEvent.ZoteroBridgeStarted);
            }
            catch (Exception exception)
            {
                _healthJournal.Record(RuntimeHealthEvent.ZoteroBridgeStartFailed, exception);
                _zoteroSelectionBridge?.Dispose();
                _zoteroSelectionBridge = null;
            }

            _trayIcon = new TrayIconManager(_settings.IsEnabled, _settings.UiLanguage);
            _trayIcon.SettingsRequested += ShowSettings;
            _trayIcon.EnabledChanged += SetEnabled;
            _trayIcon.TranslateClipboardRequested += TranslateClipboard;
            _trayIcon.DiagnosticsRequested += CopyPerformanceDiagnostics;
            _trayIcon.RestartInputCaptureRequested += RepairInputCapture;
            _trayIcon.AboutRequested += ShowAbout;
            _trayIcon.ExitRequested += ExitApplication;

            _hotkeyManager = new GlobalHotkeyManager();
            _hotkeyManager.TranslateClipboardRequested += TranslateClipboard;
            _hotkeyManager.HotkeyStoppedUnexpectedly += OnHotkeyStoppedUnexpectedly;
            try
            {
                _hotkeyManager.Start();
                _healthJournal.Record(RuntimeHealthEvent.HotkeyStarted);
            }
            catch (Exception exception)
            {
                _healthJournal.Record(RuntimeHealthEvent.HotkeyRegistrationFailed, exception);
                System.Diagnostics.Debug.WriteLine($"Yita hotkey registration failed: {exception}");
                _hotkeyManager.TranslateClipboardRequested -= TranslateClipboard;
                _hotkeyManager.HotkeyStoppedUnexpectedly -= OnHotkeyStoppedUnexpectedly;
                _hotkeyManager.Dispose();
                _hotkeyManager = null;
                _trayIcon.ShowError(L(
                    "Could not register Ctrl+Shift+T. Another app may already use it.",
                    "无法注册 Ctrl+Shift+T，可能已被其他程序占用。"));
            }

            if (!isVisualTest)
            {
                _singleInstance?.StartListening(
                    () => Dispatcher.BeginInvoke(ShowSettings, DispatcherPriority.Send));
            }

            StartMouseHookWithRecovery();

            if (isSmokeTest)
            {
                _smokeTestTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(750),
                };
                _smokeTestTimer.Tick += SmokeTestTimer_Tick;
                _smokeTestTimer.Start();
            }
            else if (string.Equals(_settings.ProviderId, "deepseek", StringComparison.OrdinalIgnoreCase)
                     && string.IsNullOrWhiteSpace(_settings.DeepSeekApiKey))
            {
                ScheduleCredentialRecovery(TimeSpan.FromMilliseconds(50));
            }
        }
        catch (Exception exception)
        {
            _healthJournal.Record(RuntimeHealthEvent.StartupFailed, exception);
            System.Diagnostics.Debug.WriteLine($"Yita startup failed: {exception}");
            WpfMessageBox.Show(
                L(
                    "Yita could not start. Close other instances and try again.",
                    "Yita 无法启动，请关闭其他实例后重试。"),
                L("Startup failed", "启动失败"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _exitCode = 1;
            ExitApplication();
        }
    }

    private void OnTranslationFailed(string message)
    {
        Dispatcher.BeginInvoke(() => _trayIcon?.ShowError(message));
    }

    private void OnHotkeyStoppedUnexpectedly()
    {
        _healthJournal.Record(RuntimeHealthEvent.HotkeyStoppedUnexpectedly);
        Dispatcher.BeginInvoke(
            () =>
            {
                if (_isExiting)
                {
                    return;
                }

                var previous = _hotkeyManager;
                if (previous is not null)
                {
                    previous.TranslateClipboardRequested -= TranslateClipboard;
                    previous.HotkeyStoppedUnexpectedly -= OnHotkeyStoppedUnexpectedly;
                    previous.Dispose();
                }

                var replacement = new GlobalHotkeyManager();
                replacement.TranslateClipboardRequested += TranslateClipboard;
                replacement.HotkeyStoppedUnexpectedly += OnHotkeyStoppedUnexpectedly;
                try
                {
                    replacement.Start();
                    _hotkeyManager = replacement;
                    _healthJournal.Record(RuntimeHealthEvent.HotkeyRecovered);
                }
                catch (Exception exception)
                {
                    replacement.TranslateClipboardRequested -= TranslateClipboard;
                    replacement.HotkeyStoppedUnexpectedly -= OnHotkeyStoppedUnexpectedly;
                    replacement.Dispose();
                    _hotkeyManager = null;
                    _healthJournal.Record(RuntimeHealthEvent.HotkeyRegistrationFailed, exception);
                }
            },
            DispatcherPriority.Send);
    }

    private void StartMouseHookWithRecovery()
    {
        try
        {
            _mouseHook?.Start();
            _mouseHookRecoveryFailures = 0;
            _healthJournal.Record(RuntimeHealthEvent.MouseHookStarted);

            // Startup applications can be launched while Windows is still
            // completing the interactive desktop. Rebinding once shortly after
            // launch prevents a stale early hook from making the app appear to
            // be running while it receives no gestures.
            ScheduleMouseHookRecovery(TimeSpan.FromSeconds(8));
        }
        catch (Exception exception)
        {
            _healthJournal.Record(RuntimeHealthEvent.MouseHookRecoveryFailed, exception);
            System.Diagnostics.Debug.WriteLine($"Yita mouse hook startup failed: {exception}");
            _trayIcon?.ShowError(L(
                "Input capture could not start. Yita will retry automatically.",
                "划词捕获暂时无法启动，Yita 将自动重试。"));
            ScheduleMouseHookRecovery(TimeSpan.FromSeconds(2));
        }
    }

    private AppSettings GetSettingsSnapshot()
    {
        return _settings;
    }

    private void ScheduleCredentialRecovery(TimeSpan delay)
    {
        if (_isExiting
            || !string.Equals(_settings.ProviderId, "deepseek", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(_settings.DeepSeekApiKey))
        {
            return;
        }

        StopCredentialRecoveryTimer();
        _credentialRecoveryTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher)
        {
            Interval = delay,
        };
        _credentialRecoveryTimer.Tick += CredentialRecoveryTimer_Tick;
        _credentialRecoveryTimer.Start();
    }

    private async void CredentialRecoveryTimer_Tick(object? sender, EventArgs e)
    {
        StopCredentialRecoveryTimer();
        if (_isExiting || _isCredentialRecoveryInProgress)
        {
            return;
        }

        var revision = _settingsRevision;
        _isCredentialRecoveryInProgress = true;
        try
        {
            var readResult = await Task.Run(
                () =>
                {
                    var succeeded = _settingsStore.TryReadApiKey(out var recoveredApiKey);
                    return (Succeeded: succeeded, ApiKey: recoveredApiKey);
                });
            if (_isExiting || revision != _settingsRevision)
            {
                return;
            }

            if (readResult.Succeeded)
            {
                _credentialRecoveryFailures = 0;
                _settings = _settings with { DeepSeekApiKey = readResult.ApiKey };
                _healthJournal.Record(RuntimeHealthEvent.CredentialRecovered);
                if (string.IsNullOrWhiteSpace(readResult.ApiKey)
                    && string.Equals(_settings.ProviderId, "deepseek", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Dispatcher.BeginInvoke(ShowSettings, DispatcherPriority.ApplicationIdle);
                }

                return;
            }

            _credentialRecoveryFailures++;
            _healthJournal.Record(
                RuntimeHealthEvent.CredentialReadFailed,
                numericCode: _credentialRecoveryFailures);
            var retrySeconds = Math.Min(30, 2 + (_credentialRecoveryFailures * 3));
            ScheduleCredentialRecovery(TimeSpan.FromSeconds(retrySeconds));
        }
        finally
        {
            _isCredentialRecoveryInProgress = false;
        }
    }

    private void StopCredentialRecoveryTimer()
    {
        if (_credentialRecoveryTimer is null)
        {
            return;
        }

        _credentialRecoveryTimer.Stop();
        _credentialRecoveryTimer.Tick -= CredentialRecoveryTimer_Tick;
        _credentialRecoveryTimer = null;
    }

    private void OnMouseHookStoppedUnexpectedly()
    {
        _healthJournal.Record(RuntimeHealthEvent.MouseHookStoppedUnexpectedly);
        Dispatcher.BeginInvoke(
            () =>
            {
                if (_isExiting)
                {
                    return;
                }

                System.Diagnostics.Debug.WriteLine("Yita mouse hook stopped unexpectedly; scheduling recovery.");
                ScheduleMouseHookRecovery(TimeSpan.FromSeconds(1));
            },
            DispatcherPriority.Send);
    }

    private void RepairInputCapture()
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                if (_isExiting || _mouseHook is null)
                {
                    return;
                }

                _mouseHookRecoveryFailures = 0;
                ScheduleMouseHookRecovery(TimeSpan.FromMilliseconds(50));
                _trayIcon?.ShowInfo(
                    "Yita",
                    L(
                        "Refreshing input capture. Select text again in a moment.",
                        "正在刷新划词捕获，请稍候再次选中文字。"));
            },
            DispatcherPriority.Send);
    }

    private void ScheduleMouseHookRecovery(TimeSpan delay)
    {
        if (_isExiting || _mouseHook is null)
        {
            return;
        }

        if (_mouseHookRecoveryTimer is not null)
        {
            _mouseHookRecoveryTimer.Stop();
            _mouseHookRecoveryTimer.Tick -= MouseHookRecoveryTimer_Tick;
        }

        _mouseHookRecoveryTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher)
        {
            Interval = delay,
        };
        _mouseHookRecoveryTimer.Tick += MouseHookRecoveryTimer_Tick;
        _mouseHookRecoveryTimer.Start();
    }

    private async void MouseHookRecoveryTimer_Tick(object? sender, EventArgs e)
    {
        if (_mouseHookRecoveryTimer is not null)
        {
            _mouseHookRecoveryTimer.Stop();
            _mouseHookRecoveryTimer.Tick -= MouseHookRecoveryTimer_Tick;
            _mouseHookRecoveryTimer = null;
        }

        if (_isExiting || _isMouseHookRecoveryInProgress || _mouseHook is null)
        {
            return;
        }

        var hook = _mouseHook;
        _isMouseHookRecoveryInProgress = true;
        try
        {
            await Task.Run(hook.Restart);
            _mouseHookRecoveryFailures = 0;
            _healthJournal.Record(RuntimeHealthEvent.MouseHookRecoverySucceeded);
        }
        catch (Exception exception)
        {
            _healthJournal.Record(RuntimeHealthEvent.MouseHookRecoveryFailed, exception);
            System.Diagnostics.Debug.WriteLine($"Yita mouse hook recovery failed: {exception}");
            _mouseHookRecoveryFailures++;
            var retrySeconds = Math.Min(15, 2 + (_mouseHookRecoveryFailures * 2));
            ScheduleMouseHookRecovery(TimeSpan.FromSeconds(retrySeconds));
        }
        finally
        {
            _isMouseHookRecoveryInProgress = false;
            if (!_isExiting && _mouseHookRecoveryFailures == 0)
            {
                // Windows can silently remove WH_MOUSE_LL after a callback
                // timeout without ending our message loop. The callback is now
                // tiny, and this low-frequency rebind covers the undetectable
                // residual case as well as long-running desktop sessions.
                ScheduleMouseHookRecovery(PeriodicMouseHookRebindInterval);
            }
        }
    }

    private async Task RunPopupSmokeTestAsync()
    {
        try
        {
            var anchor = new ScreenPoint(160, 140);
            _popupManager?.ShowLoading(long.MaxValue, anchor);
            await Task.Delay(180);
            if (_popupManager?.GetWindowForVisualTest(long.MaxValue)?.IsVisible == true)
            {
                throw new InvalidOperationException("首次翻译在收到译文前不应显示等待窗口。");
            }

            _popupManager?.ShowTranslation(
                long.MaxValue,
                "这是用于界面验证的中文原文。",
                "This is Chinese source text used for UI verification.",
                LanguageDirectionResolver.English,
                anchor);
            await Task.Delay(250);
            var popup = _popupManager?.GetWindowForVisualTest(long.MaxValue)
                ?? throw new InvalidOperationException("无法创建浮窗冒烟测试窗口。");
            if (!popup.IsVisible)
            {
                throw new InvalidOperationException("收到译文后浮窗没有显示。");
            }

            _popupManager.HideTransientPopupIfOutside(new ScreenPoint(-32000, -32000));
            await Task.Delay(80);
            if (popup.IsVisible)
            {
                throw new InvalidOperationException("未置顶浮窗没有在外部点击路径中关闭。");
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Yita popup smoke test failed: {exception}");
            _exitCode = 1;
        }
        finally
        {
            ExitApplication();
        }
    }

    private async Task RunPopupSnapshotTestAsync()
    {
        const long requestId = long.MaxValue - 1;
        try
        {
            ThemeManager.Apply(AppSettings.Default);
            var anchor = new ScreenPoint(220, 170);
            _popupManager?.ShowTranslation(
                requestId,
                "简约不是减少功能，而是让每一次操作都更直接。",
                "Simplicity is not about [[h1:removing capability]]; it is about making every action feel [[h2:direct and effortless]].",
                LanguageDirectionResolver.English,
                anchor);
            _popupManager?.CompleteRequest(requestId);
            await Task.Delay(350);
            var window = _popupManager?.GetWindowForVisualTest(requestId)
                ?? throw new InvalidOperationException("无法创建浮窗预览。");
            if (!window.HasUsableDragHandleForVisualTest())
            {
                throw new InvalidOperationException("浮窗顶部拖拽区尺寸不足或不可点击。");
            }

            if (!window.HasMinimalReadingToolbarForVisualTest())
            {
                throw new InvalidOperationException("翻译浮窗未显示精简的原文/译文、固定和关闭工具栏。");
            }

            if (!window.HasSingleRowExternalControlsForVisualTest())
            {
                throw new InvalidOperationException("翻译浮窗外置控件未保持单行或发生裁切。");
            }

            if (!window.IsCodeAnalysisEnabledForVisualTest())
            {
                throw new InvalidOperationException("翻译完成后代码分析按钮未正确启用。");
            }

            if (!window.HasContentHighlightsForVisualTest())
            {
                throw new InvalidOperationException("AI 标注没有转换为翻译正文的高亮样式。");
            }

            if (window.CurrentTranslationText.Contains("[[h", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AI 标注协议泄漏到了可复制译文。");
            }

            VisualSnapshotRenderer.SavePng(window, GetSnapshotPath("popup"));
            window.SetFontSizeForVisualTest(22);
            await Task.Delay(100);
            if (Math.Abs(window.TranslationFontSizeForVisualTest - 22) > 0.1)
            {
                throw new InvalidOperationException("翻译浮窗字号滑杆未即时更新正文。");
            }

            VisualSnapshotRenderer.SavePng(window, GetSnapshotPath("popup-font-large"));
            window.SetFontSizeForVisualTest(16.5);

            ThemeManager.Apply(AppSettings.Default with
            {
                HighlightPalette = HighlightPaletteCatalog.MorandiPaletteId,
            });
            await Task.Delay(80);
            VisualSnapshotRenderer.SavePng(window, GetSnapshotPath("popup-highlights-morandi"));

            ThemeManager.Apply(AppSettings.Default with
            {
                PopupVisualStyle = PopupVisualStyleCatalog.BubbleStyleId,
            });
            window.ApplyAppearance(
                TranslationFontCatalog.DefaultEnglishFontFamily,
                TranslationFontCatalog.DefaultChineseFontFamily,
                UiLanguageCatalog.EnglishLanguageId,
                PopupVisualStyleCatalog.BubbleStyleId);
            await Task.Delay(120);
            if (!window.IsBubbleVisualStyleForVisualTest())
            {
                throw new InvalidOperationException("气泡浮窗样式未应用。");
            }

            VisualSnapshotRenderer.SavePng(window, GetSnapshotPath("popup-bubble"));

            ThemeManager.Apply(AppSettings.Default with
            {
                PopupVisualStyle = PopupVisualStyleCatalog.BubbleV2StyleId,
            });
            window.ApplyAppearance(
                TranslationFontCatalog.DefaultEnglishFontFamily,
                TranslationFontCatalog.DefaultChineseFontFamily,
                UiLanguageCatalog.EnglishLanguageId,
                PopupVisualStyleCatalog.BubbleV2StyleId);
            await Task.Delay(120);
            if (!window.IsBubbleV2VisualStyleForVisualTest())
            {
                throw new InvalidOperationException("气泡 2.0 浮窗样式未应用。");
            }

            VisualSnapshotRenderer.SavePng(window, GetSnapshotPath("popup-bubble-v2"));

            await RunGlassSnapshotChecksAsync(window);

            ThemeManager.Apply(AppSettings.Default);
            window.ApplyAppearance(
                TranslationFontCatalog.DefaultEnglishFontFamily,
                TranslationFontCatalog.DefaultChineseFontFamily,
                UiLanguageCatalog.EnglishLanguageId,
                PopupVisualStyleCatalog.MinimalStyleId);

            window.ApplyUiLanguage(UiLanguageCatalog.SimplifiedChineseLanguageId);
            await Task.Delay(100);
            VisualSnapshotRenderer.SavePng(window, GetSnapshotPath("popup-zh"));
            window.ApplyUiLanguage(UiLanguageCatalog.EnglishLanguageId);

            window.SelectTextForVisualTest(0, 34);
            await Task.Delay(120);
            if (!window.HasActiveSelectionForVisualTest())
            {
                throw new InvalidOperationException("浮窗正文未进入正常文字选择状态。");
            }

            if (!window.HasSelectionExplainButtonForVisualTest())
            {
                throw new InvalidOperationException("译文选区未显示解释按钮。");
            }

            VisualSnapshotRenderer.SavePng(window, GetSnapshotPath("popup-selection"));

            window.ShowExplanationLoading();
            window.ShowExplanation(
                "释义：这句话强调[[h1:简约并非删减能力]]，而是降低操作阻力。\n\n要点：[[h2:capability]] 指功能能力；direct and effortless 表示直接、无需额外心智负担。\n\n语境：适合用于产品设计、交互体验或工作流程的说明。");
            window.MarkExplanationComplete();
            await Task.Delay(420);
            if (!window.IsExplanationVisibleForVisualTest())
            {
                throw new InvalidOperationException("AI 解释覆盖层未显示。");
            }

            if (!window.HasExplanationRecordButtonForVisualTest()
                || !window.IsExplanationRecordEnabledForVisualTest())
            {
                throw new InvalidOperationException("AI 解释完成后 Record 按钮未正确启用。");
            }

            if (!window.HasContentHighlightsForVisualTest())
            {
                throw new InvalidOperationException("AI 标注没有转换为解释正文的高亮样式。");
            }

            if (window.CurrentExplanationText.Contains("[[h", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AI 标注协议泄漏到了可复制解释。");
            }

            VisualSnapshotRenderer.SavePng(window, GetSnapshotPath("popup-explanation"));
            window.HideExplanation(notifyDismissed: false);

            window.StartCodeAnalysisForVisualTest();
            window.ShowExplanation(
                "语言判断：[[h1:C#]]，依据是 Console.WriteLine 与分号语法。\n\n常见用途：用于 .NET 控制台程序输出文本。\n\n代码作用：向标准输出写入一行内容。\n\n关键逻辑：调用 Console 类型的静态 [[h2:WriteLine]] 方法。\n\n替代与注意：可使用日志框架替代；生产环境应避免输出敏感信息。");
            window.MarkExplanationComplete();
            await Task.Delay(220);
            if (!window.IsCodeAnalysisPresentationForVisualTest())
            {
                throw new InvalidOperationException("代码分析覆盖层未正确应用标题或模式。");
            }

            VisualSnapshotRenderer.SavePng(window, GetSnapshotPath("popup-code-analysis"));
            window.HideExplanation(notifyDismissed: false);

            var questionWindow = new QuestionAnswerWindow(
                Guid.NewGuid(),
                UiLanguageCatalog.EnglishLanguageId,
                TranslationFontCatalog.DefaultEnglishFontFamily,
                TranslationFontCatalog.DefaultChineseFontFamily);
            questionWindow.ShowNear(window);
            questionWindow.BeginQuestion("Why is this wording effective?", isRetry: false);
            questionWindow.UpdateAnswer(
                "It keeps the meaning [[h1:precise]] while reducing unnecessary wording.\n\n"
                + "The contrast between [[h2:capability and effort]] makes the design principle easy to remember.");
            questionWindow.CompleteAnswer(
                "It keeps the meaning [[h1:precise]] while reducing unnecessary wording.\n\n"
                + "The contrast between [[h2:capability and effort]] makes the design principle easy to remember.");
            await Task.Delay(180);
            if (!questionWindow.HasDirectFontSizeSliderForVisualTest())
            {
                throw new InvalidOperationException("问答窗未显示可直接拖动的字号滑杆。");
            }

            if (!questionWindow.HasSingleRowExternalControlsForVisualTest())
            {
                throw new InvalidOperationException("问答窗外置控件未保持单行。");
            }

            if (!questionWindow.HasRecordButtonForVisualTest()
                || !questionWindow.IsRecordEnabledForVisualTest())
            {
                throw new InvalidOperationException("问答完成后 Record 按钮未正确启用。");
            }

            if (!questionWindow.HasContentHighlightsForVisualTest())
            {
                throw new InvalidOperationException("AI 标注没有转换为问答正文的高亮样式。");
            }

            if (questionWindow.CompletedTurns.Any(turn => turn.Content.Contains("[[h", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("AI 标注协议泄漏到了后续问答上下文。");
            }

            VisualSnapshotRenderer.SavePng(questionWindow, GetSnapshotPath("question-answer"));
            foreach (var glassStyle in new[] { PopupVisualStyleCatalog.BubbleV3StyleId, PopupVisualStyleCatalog.BubbleV3ColorStyleId })
            {
                ThemeManager.Apply(AppSettings.Default with { PopupVisualStyle = glassStyle });
                questionWindow.ApplyAppearance(
                    UiLanguageCatalog.EnglishLanguageId,
                    TranslationFontCatalog.DefaultEnglishFontFamily,
                    TranslationFontCatalog.DefaultChineseFontFamily);
                await Task.Delay(120);
                if (!questionWindow.HasUsableGlassRimForVisualTest()
                    || !questionWindow.HasReadableGlassTextSurfaceForVisualTest())
                {
                    throw new InvalidOperationException("问答窗切换气泡 3.0 后雾白正文卡片、圆角裁切或玻璃边缘没有同步更新。");
                }
                VisualSnapshotRenderer.SaveOnBackdrop(questionWindow, GetSnapshotPath($"question-answer-{glassStyle}"), dark: false);
            }
            questionWindow.ApplyAppearance(
                UiLanguageCatalog.SimplifiedChineseLanguageId,
                TranslationFontCatalog.DefaultEnglishFontFamily,
                TranslationFontCatalog.DefaultChineseFontFamily);
            await Task.Delay(100);
            VisualSnapshotRenderer.SaveOnBackdrop(questionWindow, GetSnapshotPath("question-answer-bubble-v3-color-zh"), dark: false);
            ThemeManager.Apply(AppSettings.Default);
            questionWindow.ApplyAppearance(
                UiLanguageCatalog.EnglishLanguageId,
                TranslationFontCatalog.DefaultEnglishFontFamily,
                TranslationFontCatalog.DefaultChineseFontFamily);
            if (!questionWindow.HasMatchingSurfaceClipForVisualTest())
            {
                throw new InvalidOperationException("问答窗切回简约样式后仍保留了气泡圆角裁切。");
            }
            questionWindow.SetFontSizeForVisualTest(23);
            await Task.Delay(100);
            if (Math.Abs(questionWindow.TranscriptFontSizeForVisualTest - 23) > 0.1)
            {
                throw new InvalidOperationException("问答窗字号滑杆未即时更新正文。");
            }

            VisualSnapshotRenderer.SavePng(questionWindow, GetSnapshotPath("question-answer-font-large"));
            questionWindow.ApplySizePresetForVisualTest(WindowSizePreset.Square);
            await Task.Delay(120);
            if (Math.Abs(questionWindow.ActualWidth - questionWindow.ActualHeight) > 2)
            {
                throw new InvalidOperationException("问答窗正方形尺寸预设未正确应用。");
            }

            VisualSnapshotRenderer.SavePng(questionWindow, GetSnapshotPath("question-answer-square"));
            questionWindow.ApplySizePresetForVisualTest(WindowSizePreset.Small);
            await Task.Delay(120);
            if (questionWindow.ActualWidth >= questionWindow.ActualHeight * 2)
            {
                throw new InvalidOperationException("问答窗小尺寸预设比例异常。");
            }

            VisualSnapshotRenderer.SavePng(questionWindow, GetSnapshotPath("question-answer-small"));
            questionWindow.Close();

            _popupManager.OpenDeepSeekChatForVisualTest(requestId);
            await Task.Delay(160);
            var deepSeekChatWindow = _popupManager.GetDeepSeekChatWindowForVisualTest()
                ?? throw new InvalidOperationException("无法创建 DeepSeek 快速聊天窗。");
            if (!deepSeekChatWindow.IsDeepSeekQuickChat || !deepSeekChatWindow.IsPinned)
            {
                throw new InvalidOperationException("DeepSeek 快速聊天窗未以默认置顶模式打开。");
            }

            if (!deepSeekChatWindow.HasDirectFontSizeSliderForVisualTest())
            {
                throw new InvalidOperationException("DeepSeek 快速聊天窗未显示可直接拖动的字号滑杆。");
            }

            if (!deepSeekChatWindow.HasSingleRowExternalControlsForVisualTest())
            {
                throw new InvalidOperationException("DeepSeek 快速聊天窗外置控件未保持单行。");
            }

            deepSeekChatWindow.BeginQuestion("为什么天空通常是蓝色的？", isRetry: false);
            deepSeekChatWindow.CompleteAnswer(
                "阳光进入大气后，波长较短的[[h1:蓝光]]比红光更容易被空气分子散射，因此从多数方向进入眼睛的散射光以[[h2:蓝色为主]]。");
            await Task.Delay(120);
            if (!deepSeekChatWindow.HasRecordButtonForVisualTest()
                || !deepSeekChatWindow.IsRecordEnabledForVisualTest())
            {
                throw new InvalidOperationException("DeepSeek 快速聊天完成后 Record 按钮未正确启用。");
            }

            if (!deepSeekChatWindow.HasContentHighlightsForVisualTest())
            {
                throw new InvalidOperationException("DeepSeek 快速聊天没有呈现 AI 重点高亮。");
            }

            if (deepSeekChatWindow.CompletedTurns.Any(turn => turn.Content.Contains("[[h", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("AI 标注协议泄漏到了快速聊天上下文。");
            }

            VisualSnapshotRenderer.SavePng(deepSeekChatWindow, GetSnapshotPath("deepseek-chat"));
            deepSeekChatWindow.ApplySizePresetForVisualTest(WindowSizePreset.Tall);
            await Task.Delay(120);
            if (deepSeekChatWindow.ActualHeight <= deepSeekChatWindow.ActualWidth)
            {
                throw new InvalidOperationException("DeepSeek 聊天窗竖向尺寸预设未正确应用。");
            }

            VisualSnapshotRenderer.SavePng(deepSeekChatWindow, GetSnapshotPath("deepseek-chat-tall"));
            deepSeekChatWindow.Close();

            const string longTranslation =
                "A restrained interface should remain comfortable when the content grows. "
                + "The translation area now uses the available viewport instead of expanding beyond the window. "
                + "Large type stays readable, while a vertical scrollbar keeps every paragraph accessible.\n\n"
                + "简洁不等于功能受限。固定窗口尺寸后，正文会在剩余空间中自然排版；当大字号和长文本超出可视区域时，滚动条会出现，边缘拖动也会继续调整可读空间。";
            _popupManager.ShowTranslation(
                requestId,
                "用于验证长文本、大字号和窗口缩放。",
                longTranslation,
                LanguageDirectionResolver.Chinese,
                anchor);
            _popupManager.CompleteRequest(requestId);
            window.ConfigureViewportForVisualTest(width: 560, height: 260, fontSize: 30);
            await Task.Delay(220);
            if (!window.HasVerticalOverflowForVisualTest())
            {
                throw new InvalidOperationException("长文本预览未形成可滚动区域。");
            }

            VisualSnapshotRenderer.SavePng(window, GetSnapshotPath("popup-overflow"));
            window.ApplySizePresetForVisualTest(WindowSizePreset.Wide);
            await Task.Delay(120);
            if (window.ActualWidth <= window.ActualHeight)
            {
                throw new InvalidOperationException("翻译浮窗横向尺寸预设未正确应用。");
            }

            VisualSnapshotRenderer.SavePng(window, GetSnapshotPath("popup-wide"));
            var wideWidth = window.ActualWidth;
            window.ApplySizePresetForVisualTest(WindowSizePreset.Small);
            await Task.Delay(120);
            if (window.ActualWidth >= wideWidth)
            {
                throw new InvalidOperationException("翻译浮窗小尺寸预设未缩小窗口。");
            }

            if (!window.HasSingleRowExternalControlsForVisualTest())
            {
                throw new InvalidOperationException("翻译浮窗小尺寸预设裁切了单行外置控件。");
            }

            VisualSnapshotRenderer.SavePng(window, GetSnapshotPath("popup-small"));
            ApplyGlassAppearanceForVisualTest(window);
            window.ApplySizePresetForVisualTest(WindowSizePreset.Wide);
            await Task.Delay(120);
            if (!window.HasUsableGlassRimForVisualTest()
                || !window.HasReadableGlassTextSurfaceForVisualTest()
                || !window.HasVerticalOverflowForVisualTest())
            {
                throw new InvalidOperationException("气泡 3.0 缩放后玻璃边缘与正文表面没有对齐。");
            }
            VisualSnapshotRenderer.SaveOnBackdrop(window, GetSnapshotPath("popup-bubble-v3-resized"), dark: false);
            if (!window.VerifyAppearancePreservesEditForVisualTest(PopupVisualStyleCatalog.BubbleV3StyleId))
            {
                throw new InvalidOperationException("外观刷新丢失了未保存的修正、选区或撤销记录。");
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Yita popup snapshot failed: {exception}");
            _exitCode = 1;
        }
        finally
        {
            ExitApplication();
        }
    }

    private static void ApplyGlassAppearanceForVisualTest(
        PopupWindow window,
        string glassStyle = PopupVisualStyleCatalog.BubbleV3StyleId)
    {
        ThemeManager.Apply(AppSettings.Default with { PopupVisualStyle = glassStyle });
        window.ApplyAppearance(
            TranslationFontCatalog.DefaultEnglishFontFamily,
            TranslationFontCatalog.DefaultChineseFontFamily,
            UiLanguageCatalog.EnglishLanguageId,
            glassStyle);
    }

    private async Task RunGlassSnapshotChecksAsync(PopupWindow window)
    {
        foreach (var glassStyle in new[] { PopupVisualStyleCatalog.BubbleV3StyleId, PopupVisualStyleCatalog.BubbleV3ColorStyleId })
        {
            await RunGlassVariantSnapshotChecksAsync(window, glassStyle);
        }

        // Exercise system-color fallback without changing the user's OS settings.
        ThemeManager.ApplyHighContrast(Resources);
        window.ApplyAppearance(
            TranslationFontCatalog.DefaultEnglishFontFamily,
            TranslationFontCatalog.DefaultChineseFontFamily,
            UiLanguageCatalog.EnglishLanguageId,
            PopupVisualStyleCatalog.MinimalStyleId);
        await Task.Delay(100);
        VisualSnapshotRenderer.SavePng(window, GetSnapshotPath("popup-bubble-v3-system-colors"));
    }

    private async Task RunGlassVariantSnapshotChecksAsync(PopupWindow window, string glassStyle)
    {
        ApplyGlassAppearanceForVisualTest(window, glassStyle);
        window.ConfigureViewportForVisualTest(780, 236, 16.5);
        await Task.Delay(140);
        if (!window.HasUsableGlassRimForVisualTest()
            || !window.HasReadableGlassTextSurfaceForVisualTest()
            || !window.HasUsableDragHandleForVisualTest()
            || !window.HasSingleRowExternalControlsForVisualTest())
        {
            throw new InvalidOperationException("气泡 3.0 的光学边缘、拖拽区或单行控件布局异常。");
        }

        VisualSnapshotRenderer.SavePng(window, GetSnapshotPath($"popup-{glassStyle}"));
        VisualSnapshotRenderer.SaveOnBackdrop(window, GetSnapshotPath($"popup-{glassStyle}-light"), dark: false);
        VisualSnapshotRenderer.SaveOnBackdrop(window, GetSnapshotPath($"popup-{glassStyle}-dark"), dark: true);
        window.SelectTextForVisualTest(0, 34);
        await Task.Delay(140);
        if (!window.HasActiveSelectionForVisualTest() || !window.HasSelectionExplainButtonForVisualTest())
        {
            throw new InvalidOperationException("玻璃装饰影响了正文选择或选区解释按钮。");
        }
        VisualSnapshotRenderer.SaveOnBackdrop(window, GetSnapshotPath($"popup-{glassStyle}-selection"), dark: false);
        window.ShowExplanationLoading();
        if (!window.HasHiddenTranslationForExplanationForVisualTest())
        {
            throw new InvalidOperationException("透明解释加载层透出了原译文。");
        }
        window.ShowExplanation(
            "释义：[[h1:简约不是删减能力]]，而是让每次操作更直接。\n\n要点：[[h2:direct and effortless]] 强调直观、轻松，不需要额外思考。\n\n语境：适合产品设计、交互体验和工作流程的表达。");
        window.MarkExplanationComplete();
        await Task.Delay(420);
        if (!window.IsExplanationVisibleForVisualTest()
            || !window.HasUsableGlassRimForVisualTest()
            || !window.HasReadableGlassTextSurfaceForVisualTest()
            || !window.HasHiddenTranslationForExplanationForVisualTest())
        {
            throw new InvalidOperationException("气泡 3.0 解释层或玻璃边缘异常。");
        }
        VisualSnapshotRenderer.SaveOnBackdrop(window, GetSnapshotPath($"popup-{glassStyle}-explanation"), dark: false);
        window.HideExplanation(notifyDismissed: false);
        if (!window.HasActiveSelectionForVisualTest()
            || !window.HasReadableGlassTextSurfaceForVisualTest()
            || !window.HasRestoredTranslationPresentationForVisualTest())
        {
            throw new InvalidOperationException("从玻璃解释层返回后原文选区丢失。");
        }
        window.SelectTextForVisualTest(0, 0);
    }

    private async Task RunTranslationQualitySmokeTestAsync()
    {
        try
        {
            if (_translationProviderFactory is null || _settings.ProviderId != "deepseek")
            {
                throw new InvalidOperationException("DeepSeek is required for this opt-in live test.");
            }
            const string source = """
                Your project must use at least two sprites, at least one of which must not be a cat.
                
                Your project must have at least three scripts total (i.e., not necessarily three per sprite).
                
                Your project must use at least one conditional, at least one loop, and at least one variable.
                
                Your project must use at least one custom block that you have made yourself (via Make a Block), which must take at least one input.
                
                Your project should be more complex than most of those demonstrated in lecture (many of which, though instructive, were quite short) but it can be less complex than Oscartime and Ivy’s Hardest Game.
                """;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var response = new System.Text.StringBuilder();
            await foreach (var chunk in _translationProviderFactory.Create(_settings).TranslateAsync(
                               new TranslationRequest(source, _settings.SourceLanguage, LanguageDirectionResolver.Chinese), timeout.Token))
            {
                response.Append(chunk.TextDelta);
            }
            var text = HighlightMarkup.ToPlainText(response.ToString());
            if ((!text.Contains("角色", StringComparison.Ordinal) && !text.Contains("精灵", StringComparison.Ordinal))
                || !text.Contains("脚本", StringComparison.Ordinal)
                || !text.Contains("变量", StringComparison.Ordinal)
                || !text.Contains("输入", StringComparison.Ordinal)
                || TranslationOutputGuard.IsInstructionEcho(source, text))
            {
                throw new InvalidOperationException("Translation quality regression failed.");
            }
        }
        catch (Exception exception)
        {
            // No source, response or credentials are written to diagnostics.
            System.Diagnostics.Debug.WriteLine($"Translation quality test failed: {exception.GetType().Name}");
            _exitCode = 1;
        }
        finally
        {
            ExitApplication();
        }
    }

    private async Task RunAiSmokeTestAsync()
    {
        try
        {
            if (_translationProviderFactory is null
                || !string.Equals(_settings.ProviderId, "deepseek", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(_settings.DeepSeekApiKey))
            {
                throw new InvalidOperationException("DeepSeek is not fully configured.");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var answerProvider = _translationProviderFactory.CreateQuestionAnswerProvider(_settings);
            var answer = new System.Text.StringBuilder();
            await foreach (var chunk in answerProvider.AnswerAsync(
                               new QuestionAnswerRequest(
                                   "What is two plus two? Reply with only the answer.",
                                   string.Empty,
                                   string.Empty,
                                   null,
                                   string.Empty,
                                   string.Empty,
                                   UiLanguageCatalog.EnglishLanguageId,
                                   QuestionContextKind.GeneralChat,
                                   []),
                               timeout.Token))
            {
                answer.Append(chunk.TextDelta);
            }

            var summaryProvider = _translationProviderFactory.CreateSummaryProvider(_settings);
            var summary = new System.Text.StringBuilder();
            await foreach (var chunk in summaryProvider.SummarizeAsync(
                               new SummaryRequest(
                                   "schema: yita-history/v1\nOriginal: clear wording\nTranslation: 清晰的措辞",
                                   UiLanguageCatalog.EnglishLanguageId,
                                   "today"),
                               timeout.Token))
            {
                summary.Append(chunk.TextDelta);
            }

            if (answer.Length == 0 || summary.Length == 0)
            {
                throw new InvalidOperationException("DeepSeek returned an empty AI smoke-test response.");
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Yita AI smoke test failed: {exception.GetType().Name}");
            _exitCode = 1;
        }
        finally
        {
            ExitApplication();
        }
    }

    private async Task RunSettingsSnapshotTestAsync()
    {
        SettingsWindow? window = null;
        try
        {
            window = new SettingsWindow(AppSettings.Default with
            {
                UiLanguage = UiLanguageCatalog.EnglishLanguageId,
                ProviderId = "mock",
                DeepSeekApiKey = string.Empty,
                PopupVisualStyle = PopupVisualStyleCatalog.BubbleV3StyleId,
            });
            window.Show();
            await Task.Delay(350);
            if (window.FindName("PopupVisualStyleComboBox") is not System.Windows.Controls.ComboBox styleComboBox
                || styleComboBox.Items.Count != 5
                || !Equals(styleComboBox.SelectedValue, PopupVisualStyleCatalog.BubbleV3StyleId))
            {
                throw new InvalidOperationException("设置页没有保留旧样式并正确选择气泡 3.0。");
            }
            if (window.FindName("HighlightPaletteComboBox") is not System.Windows.Controls.ComboBox paletteComboBox
                || paletteComboBox.Items.Count < 5)
            {
                throw new InvalidOperationException("设置页未显示 AI 重点高亮配色选项。");
            }
            var visual = window.Content as FrameworkElement ?? window;
            VisualSnapshotRenderer.SavePng(visual, GetSnapshotPath("settings"));
            VisualSnapshotRenderer.SavePng(visual, GetSnapshotPath("settings-en"));
            if (window.FindName("SettingsScrollViewer") is System.Windows.Controls.ScrollViewer englishScrollViewer)
            {
                englishScrollViewer.ScrollToVerticalOffset(330);
                await Task.Delay(100);
                VisualSnapshotRenderer.SavePng(visual, GetSnapshotPath("settings-intelligence-en"));
                VisualSnapshotRenderer.SavePng(visual, GetSnapshotPath("settings-history-en"));
                englishScrollViewer.ScrollToVerticalOffset(760);
                await Task.Delay(100);
                VisualSnapshotRenderer.SavePng(visual, GetSnapshotPath("settings-fonts-en"));
                englishScrollViewer.ScrollToVerticalOffset(1180);
                await Task.Delay(100);
                VisualSnapshotRenderer.SavePng(visual, GetSnapshotPath("settings-appearance-en"));
                styleComboBox.SelectedValue = PopupVisualStyleCatalog.BubbleV3ColorStyleId;
                await Task.Delay(100);
                VisualSnapshotRenderer.SavePng(visual, GetSnapshotPath("settings-color-glass-en"));
            }

            window.Close();
            window = new SettingsWindow(AppSettings.Default with
            {
                UiLanguage = UiLanguageCatalog.SimplifiedChineseLanguageId,
                ProviderId = "mock",
                DeepSeekApiKey = string.Empty,
                PopupVisualStyle = PopupVisualStyleCatalog.BubbleV3ColorStyleId,
            });
            window.Show();
            await Task.Delay(250);
            visual = window.Content as FrameworkElement ?? window;
            VisualSnapshotRenderer.SavePng(visual, GetSnapshotPath("settings-zh"));
            if (window.FindName("SettingsScrollViewer") is System.Windows.Controls.ScrollViewer chineseScrollViewer)
            {
                chineseScrollViewer.ScrollToVerticalOffset(330);
                await Task.Delay(100);
                VisualSnapshotRenderer.SavePng(visual, GetSnapshotPath("settings-intelligence-zh"));
                VisualSnapshotRenderer.SavePng(visual, GetSnapshotPath("settings-history-zh"));
                chineseScrollViewer.ScrollToVerticalOffset(760);
                await Task.Delay(100);
                VisualSnapshotRenderer.SavePng(visual, GetSnapshotPath("settings-fonts-zh"));
                chineseScrollViewer.ScrollToVerticalOffset(1180);
                await Task.Delay(100);
                VisualSnapshotRenderer.SavePng(visual, GetSnapshotPath("settings-appearance-zh"));
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Yita settings snapshot failed: {exception}");
            _exitCode = 1;
        }
        finally
        {
            window?.Close();
            ExitApplication();
        }
    }

    private async Task RunSettingsLifecycleTestAsync()
    {
        try
        {
            // Warm WPF/font caches before measuring retained native resources.
            for (var index = 0; index < 2; index++)
            {
                await ShowAndCloseSettingsOffscreenAsync();
            }

            ForceFullCollection();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            process.Refresh();
            var plainBaselineHandles = process.HandleCount;
            for (var index = 0; index < 30; index++)
            {
                await ShowAndClosePlainWindowOffscreenAsync();
            }

            await CompleteWindowResourceCleanupAsync();
            process.Refresh();
            var plainWindowGrowth = process.HandleCount - plainBaselineHandles;
            _healthJournal.Record(
                RuntimeHealthEvent.PlainWindowLifecycleCheck,
                numericCode: plainWindowGrowth);

            var secondPlainBaselineHandles = process.HandleCount;
            for (var index = 0; index < 30; index++)
            {
                await ShowAndClosePlainWindowOffscreenAsync();
            }

            await CompleteWindowResourceCleanupAsync();
            process.Refresh();
            var secondPlainWindowGrowth = process.HandleCount - secondPlainBaselineHandles;
            _healthJournal.Record(
                RuntimeHealthEvent.PlainWindowSecondLifecycleCheck,
                numericCode: secondPlainWindowGrowth);

            var baselineHandles = process.HandleCount;
            var baselineGdi = NativeMethods.GetGuiResources(process.Handle, NativeMethods.GrGdiObjects);
            var baselineUser = NativeMethods.GetGuiResources(process.Handle, NativeMethods.GrUserObjects);
            for (var index = 0; index < 30; index++)
            {
                await ShowAndCloseSettingsOffscreenAsync();
            }

            await CompleteWindowResourceCleanupAsync();
            process.Refresh();
            var retainedHandleGrowth = process.HandleCount - baselineHandles;
            var retainedGdiGrowth = (int)NativeMethods.GetGuiResources(
                process.Handle,
                NativeMethods.GrGdiObjects) - (int)baselineGdi;
            var retainedUserGrowth = (int)NativeMethods.GetGuiResources(
                process.Handle,
                NativeMethods.GrUserObjects) - (int)baselineUser;
            _healthJournal.Record(
                RuntimeHealthEvent.SettingsLifecycleCheck,
                numericCode: retainedHandleGrowth);
            _healthJournal.Record(
                RuntimeHealthEvent.SettingsLifecycleGdiCheck,
                numericCode: retainedGdiGrowth);
            _healthJournal.Record(
                RuntimeHealthEvent.SettingsLifecycleUserCheck,
                numericCode: retainedUserGrowth);
            // WPF/MILCore retains a bounded native window cache even for empty
            // windows. Fail only if Settings retains materially more than a
            // same-sized second batch of plain WPF windows.
            if (retainedHandleGrowth > secondPlainWindowGrowth + 12
                || retainedGdiGrowth > 2
                || retainedUserGrowth > 2)
            {
                throw new InvalidOperationException(
                    $"Settings lifecycle retained {retainedHandleGrowth} handles after collection.");
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Yita settings lifecycle test failed: {exception}");
            _exitCode = 1;
        }
        finally
        {
            ExitApplication();
        }
    }

    private async Task ShowAndCloseSettingsOffscreenAsync()
    {
        var window = new SettingsWindow(AppSettings.Default)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32_000,
            Top = -32_000,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        window.Show();
        await Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.Loaded);
        window.Close();
        await Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.ApplicationIdle);
    }

    private async Task ShowAndClosePlainWindowOffscreenAsync()
    {
        var window = new Window
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32_000,
            Top = -32_000,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 200,
            Height = 100,
        };
        window.Show();
        await Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.Loaded);
        window.Close();
        await Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.ApplicationIdle);
    }

    private async Task CompleteWindowResourceCleanupAsync()
    {
        // MILCore releases some window/render resources asynchronously on its
        // composition thread. Give that cleanup a bounded opportunity before
        // treating retained kernel handles as an application leak.
        await Task.Delay(TimeSpan.FromSeconds(2));
        await Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.ApplicationIdle);
        ForceFullCollection();
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        ForceFullCollection();
    }

    private static void ForceFullCollection()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static string GetSnapshotPath(string name)
    {
        return System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"Yita-{name}-preview.png");
    }

    private PopupManager CreatePopupManager()
    {
        return new PopupManager(
            () => new PopupAppearanceSettings(
                _settings.DefaultTranslationFontSize,
                _settings.EnglishTranslationFontFamily,
                _settings.ChineseTranslationFontFamily,
                _settings.UiLanguage,
                _settings.PopupVisualStyle),
            (request, cancellationToken) =>
                _aiHistoryStore.AppendManualRecordAsync(_settings, request, cancellationToken));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeServicesSafely();
        _healthJournal.Record(RuntimeHealthEvent.AppExited, numericCode: e.ApplicationExitCode);
        base.OnExit(e);
    }

    private void SubscribeRuntimeDiagnostics()
    {
        if (_isRuntimeDiagnosticsSubscribed)
        {
            return;
        }

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        _isRuntimeDiagnosticsSubscribed = true;
    }

    private void App_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        _healthJournal.Record(RuntimeHealthEvent.DispatcherUnhandledException, e.Exception);
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _healthJournal.Record(
            RuntimeHealthEvent.ProcessUnhandledException,
            e.ExceptionObject as Exception,
            numericCode: e.IsTerminating ? 1 : 0);
    }

    private void TaskScheduler_UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        _healthJournal.Record(RuntimeHealthEvent.UnobservedTaskException, e.Exception);
        e.SetObserved();
    }

    private void ShowSettings()
    {
        Dispatcher.Invoke(() =>
        {
            if (_settingsWindow is not null)
            {
                if (_settingsWindow.WindowState == WindowState.Minimized)
                {
                    _settingsWindow.WindowState = WindowState.Normal;
                }

                _settingsWindow.Activate();
                return;
            }

            var settingsSnapshot = GetSettingsSnapshot();
            var preserveCredentialOnBlank = _settingsStore.ApiKeyReadFailed
                                            || _isCredentialRecoveryInProgress;
            var settingsWindow = new SettingsWindow(
                settingsSnapshot,
                () => _translationMemoryStore.Count,
                _translationMemoryStore.Clear,
                (settings, range, cancellationToken) => _aiSummaryService is null
                    ? Task.FromResult(new AiSummaryGenerationResult(false, 0, ErrorCode: "summary_unavailable"))
                    : _aiSummaryService.GenerateAsync(settings, range, cancellationToken));
            _settingsWindow = settingsWindow;
            bool? result;
            try
            {
                result = settingsWindow.ShowDialog();
            }
            finally
            {
                _settingsWindow = null;
            }

            if (result != true || settingsWindow.ResultSettings is null)
            {
                return;
            }

            try
            {
                var persistApiKey = settingsWindow.ApiKeyClearRequested
                                    || !preserveCredentialOnBlank
                                    || !string.IsNullOrWhiteSpace(settingsWindow.ResultSettings.DeepSeekApiKey);
                var resultSettings = settingsWindow.ResultSettings;
                if (!persistApiKey && string.IsNullOrWhiteSpace(resultSettings.DeepSeekApiKey))
                {
                    // A credential recovery can finish while ShowDialog runs its
                    // nested dispatcher loop. Never let the dialog's stale blank
                    // field erase that recovered in-memory or stored secret.
                    resultSettings = resultSettings with
                    {
                        DeepSeekApiKey = _settings.DeepSeekApiKey,
                    };
                }

                _startupRegistration.SetEnabled(resultSettings.StartWithWindows);
                _settingsStore.Save(
                    resultSettings,
                    persistApiKey);
                _settings = resultSettings;
                _settingsRevision++;
                if (!persistApiKey)
                {
                    ScheduleCredentialRecovery(TimeSpan.FromSeconds(2));
                }
                ThemeManager.Apply(_settings);
                _popupManager?.ApplyAppearanceToOpenWindows();
                _trayIcon?.ApplyUiLanguage(_settings.UiLanguage);
                _trayIcon?.SetEnabled(_settings.IsEnabled);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Yita settings save failed: {exception}");
                try
                {
                    _startupRegistration.SetEnabled(_settings.StartWithWindows);
                    _settingsStore.Save(_settings, persistApiKey: false);
                }
                catch (Exception rollbackException)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Yita settings rollback failed: {rollbackException}");
                }

                if (!_isCredentialRecoveryInProgress
                    && string.Equals(_settings.ProviderId, "deepseek", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(_settings.DeepSeekApiKey))
                {
                    ScheduleCredentialRecovery(TimeSpan.FromSeconds(2));
                }

                WpfMessageBox.Show(
                    settingsWindow,
                    L(
                        "Could not save settings. Check file permissions and try again.",
                        "设置保存失败，请检查文件权限后重试。"),
                    "Yita",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        });
    }

    private void TranslateClipboard()
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                try
                {
                    var text = WpfClipboard.ContainsText(WpfTextDataFormat.UnicodeText)
                        ? TextNormalizer.Normalize(WpfClipboard.GetText(WpfTextDataFormat.UnicodeText))
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        _trayIcon?.ShowInfo(
                            "Yita",
                            L("The clipboard does not contain text to translate.", "剪贴板中没有可翻译的文字。"));
                        return;
                    }

                    var anchor = NativeMethods.GetCursorPos(out var point)
                        ? point.ToScreenPoint()
                        : new ScreenPoint(160, 140);
                    _coordinator?.TranslateText(text, anchor);
                }
                catch (ExternalException)
                {
                    _trayIcon?.ShowError(L(
                        "The clipboard is temporarily unavailable. Copy the text and try again.",
                        "暂时无法读取剪贴板，请复制文字后重试。"));
                }
            },
            DispatcherPriority.Send);
    }

    private void OnZoteroSelectionReceived(string text)
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                if (_isExiting || !_settings.IsEnabled || _coordinator is null)
                {
                    return;
                }

                var anchor = NativeMethods.GetCursorPos(out var point)
                    ? point.ToScreenPoint()
                    : new ScreenPoint(160, 140);
                _coordinator.TranslateText(text, anchor, TranslationTrigger.ZoteroIntegration);
            },
            DispatcherPriority.Send);
    }

    private void ShowAbout()
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                WpfMessageBox.Show(
                    $"Yita {version?.Major ?? 0}.{version?.Minor ?? 0}.{version?.Build ?? 0}\n\n"
                    + L(
                        "Translations are powered by DeepSeek. Source text and translations are not saved by default. Only corrections you explicitly save are encrypted for your Windows account and can be cleared in Settings.",
                        "由 DeepSeek 提供翻译。默认不保存原文和译文；只有你主动保存的修正译文会为当前 Windows 账户加密，并可在设置中清除。"),
                    L("About Yita", "关于 Yita"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            });
    }

    private void CopyPerformanceDiagnostics()
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                try
                {
                    var useChinese = _settings.UiLanguage == UiLanguageCatalog.SimplifiedChineseLanguageId;
                    var inputStatus = _mouseHook?.CreateStatusReport(useChinese)
                                      ?? (useChinese
                                          ? "输入捕获状态\n全局鼠标钩子：不可用"
                                          : "Input capture status\nGlobal mouse hook: unavailable");
                    var selectionStatus = _uiaSelectionReader?.CreateStatusReport(useChinese)
                                          ?? (useChinese
                                              ? "UI Automation 取词状态\n不可用"
                                              : "UI Automation selection status\nUnavailable");
                    var popupStatus = _popupManager?.CreateStatusReport(useChinese)
                                      ?? (useChinese
                                          ? "浮窗状态\n不可用"
                                          : "Popup status\nUnavailable");
                    var zoteroStatus = _zoteroSelectionBridge?.CreateStatusReport(useChinese)
                                       ?? (useChinese
                                           ? "Zotero PDF 桥接状态\n不可用"
                                           : "Zotero PDF bridge status\nUnavailable");
                    WpfClipboard.SetText(string.Join(
                        Environment.NewLine + Environment.NewLine,
                        _performanceMonitor.CreateReport(useChinese),
                        inputStatus,
                        selectionStatus,
                        zoteroStatus,
                        popupStatus,
                        _healthJournal.CreateReport(useChinese)));
                    _trayIcon?.ShowInfo(
                        "Yita",
                        L(
                            "Diagnostics copied. No selected text, translations, or credentials are included.",
                            "诊断信息已复制，不包含原文、译文或凭据。"));
                }
                catch (ExternalException)
                {
                    _trayIcon?.ShowError(L(
                        "The clipboard is temporarily unavailable. Try again.",
                        "剪贴板暂时不可用，请稍后重试。"));
                }
            },
            DispatcherPriority.Send);
    }

    private void SetEnabled(bool isEnabled)
    {
        Dispatcher.Invoke(() =>
        {
            var updated = _settings with { IsEnabled = isEnabled };
            try
            {
                _settingsStore.Save(updated, persistApiKey: false);
                _settings = updated;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Yita enabled-state save failed: {exception}");
                _trayIcon?.SetEnabled(_settings.IsEnabled);
                WpfMessageBox.Show(
                    L(
                        "Could not save the enabled state. Try again.",
                        "无法保存启用状态，请重试。"),
                    "Yita",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        });
    }

    private void ExitApplication()
    {
        Dispatcher.Invoke(() =>
        {
            if (_isExiting)
            {
                return;
            }

            _isExiting = true;
            _healthJournal.Record(RuntimeHealthEvent.ShutdownRequested, numericCode: _exitCode);
            DisposeServicesSafely();
            Shutdown(_exitCode);
        });
    }

    private void SmokeTestTimer_Tick(object? sender, EventArgs e)
    {
        if (_smokeTestTimer is not null)
        {
            _smokeTestTimer.Stop();
            _smokeTestTimer.Tick -= SmokeTestTimer_Tick;
            _smokeTestTimer = null;
        }

        if (_mouseHookRecoveryTimer is not null)
        {
            _mouseHookRecoveryTimer.Stop();
            _mouseHookRecoveryTimer.Tick -= MouseHookRecoveryTimer_Tick;
            _mouseHookRecoveryTimer = null;
        }

        ExitApplication();
    }

    private string L(string english, string chinese)
    {
        return _settings.UiLanguage == UiLanguageCatalog.SimplifiedChineseLanguageId
            ? chinese
            : english;
    }

    private void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(SystemParameters.HighContrast), StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () =>
            {
                ThemeManager.Apply(_settings);
                _popupManager?.ApplyAppearanceToOpenWindows();
            },
            DispatcherPriority.Normal);
    }

    private void TrySubscribeSessionEvents()
    {
        try
        {
            SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
            _isSessionEventsSubscribed = true;
        }
        catch (Exception exception)
        {
            SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            System.Diagnostics.Debug.WriteLine(
                $"Yita could not subscribe to session recovery events: {exception}");
        }
    }

    private void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionLogon
            or SessionSwitchReason.SessionUnlock
            or SessionSwitchReason.RemoteConnect))
        {
            return;
        }

        QueueInputRecoveryAfterSystemTransition();
    }

    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            QueueInputRecoveryAfterSystemTransition();
        }
    }

    private void QueueInputRecoveryAfterSystemTransition()
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                if (!_isExiting)
                {
                    ScheduleMouseHookRecovery(TimeSpan.FromSeconds(1));
                }
            },
            DispatcherPriority.ApplicationIdle);
    }

    private void DisposeServices()
    {
        // Release the per-session lifetime claim first. This makes an immediate
        // relaunch reliable even if a WPF window or native input service needs a
        // little longer to finish its own cleanup.
        var singleInstance = _singleInstance;
        _singleInstance = null;
        try
        {
            singleInstance?.Dispose();
        }
        catch (Exception exception)
        {
            _healthJournal.Record(RuntimeHealthEvent.ServiceDisposeFailed, exception, numericCode: 1);
        }

        if (_isRuntimeDiagnosticsSubscribed)
        {
            DispatcherUnhandledException -= App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
            _isRuntimeDiagnosticsSubscribed = false;
        }

        if (_isSessionEventsSubscribed)
        {
            SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            _isSessionEventsSubscribed = false;
        }

        if (_isSystemParametersSubscribed)
        {
            SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
            _isSystemParametersSubscribed = false;
        }

        if (_smokeTestTimer is not null)
        {
            _smokeTestTimer.Stop();
            _smokeTestTimer.Tick -= SmokeTestTimer_Tick;
            _smokeTestTimer = null;
        }

        if (_mouseHookRecoveryTimer is not null)
        {
            _mouseHookRecoveryTimer.Stop();
            _mouseHookRecoveryTimer.Tick -= MouseHookRecoveryTimer_Tick;
            _mouseHookRecoveryTimer = null;
        }

        StopCredentialRecoveryTimer();

        if (_zoteroSelectionBridge is not null)
        {
            _zoteroSelectionBridge.SelectionReceived -= OnZoteroSelectionReceived;
            _zoteroSelectionBridge.Dispose();
            _zoteroSelectionBridge = null;
        }

        if (_coordinator is not null)
        {
            _coordinator.TranslationFailed -= OnTranslationFailed;
            _coordinator.Dispose();
            _coordinator = null;
        }

        _uiaSelectionReader?.Dispose();
        _uiaSelectionReader = null;

        _translationProviderFactory?.Dispose();
        _translationProviderFactory = null;

        if (_mouseHook is not null)
        {
            _mouseHook.HookStoppedUnexpectedly -= OnMouseHookStoppedUnexpectedly;
            _mouseHook.Dispose();
            _mouseHook = null;
        }

        if (_hotkeyManager is not null)
        {
            _hotkeyManager.TranslateClipboardRequested -= TranslateClipboard;
            _hotkeyManager.HotkeyStoppedUnexpectedly -= OnHotkeyStoppedUnexpectedly;
            _hotkeyManager.Dispose();
            _hotkeyManager = null;
        }

        if (_trayIcon is not null)
        {
            _trayIcon.SettingsRequested -= ShowSettings;
            _trayIcon.EnabledChanged -= SetEnabled;
            _trayIcon.TranslateClipboardRequested -= TranslateClipboard;
            _trayIcon.DiagnosticsRequested -= CopyPerformanceDiagnostics;
            _trayIcon.RestartInputCaptureRequested -= RepairInputCapture;
            _trayIcon.AboutRequested -= ShowAbout;
            _trayIcon.ExitRequested -= ExitApplication;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _popupManager?.Dispose();
        _popupManager = null;

    }

    private void DisposeServicesSafely()
    {
        try
        {
            DisposeServices();
        }
        catch (Exception exception)
        {
            _healthJournal.Record(RuntimeHealthEvent.ServiceDisposeFailed, exception, numericCode: 2);
            System.Diagnostics.Debug.WriteLine(
                $"Yita service cleanup failed: {exception}");
        }
    }
}
