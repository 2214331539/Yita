using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Yita.Core.Platform;

namespace Yita.Desktop;

public sealed class App : Application
{
    private ISelectionRuntime? _selectionRuntime;
    private ISingleInstanceGuard? _singleInstance;
    private YitaTrayController? _tray;
    private bool _allowWindowClose;
    private MainWindow? _settingsWindow;
    private Avalonia.Threading.DispatcherTimer? _displayPreferencesTimer;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Resources["InterfaceFont"] = DesktopFontResolver.InterfaceFont;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            RefreshDisplayPreferences();
            var platform = new DesktopPlatformServices();
            _singleInstance = platform.AcquireInstance(requestActivation:
                desktop.Args?.Contains("--background", StringComparer.OrdinalIgnoreCase) != true);
            if (!_singleInstance.IsOwner)
            {
                _singleInstance.Dispose();
                _singleInstance = null;
                desktop.Shutdown(0);
                return;
            }
            try
            {
                _selectionRuntime = platform.CreateSelectionRuntime();
                _selectionRuntime?.Start();
            }
            catch
            {
                _selectionRuntime?.Dispose();
                _selectionRuntime = null;
            }

            var mainWindow = new MainWindow(_selectionRuntime, startupRegistration: platform.Startup);
            _settingsWindow = mainWindow;
            _displayPreferencesTimer = new() { Interval = TimeSpan.FromSeconds(2) };
            _displayPreferencesTimer.Tick += (_, _) => RefreshDisplayPreferences();
            _displayPreferencesTimer.Start();
            desktop.MainWindow = mainWindow;
            mainWindow.UiLanguageChanged += (_, language) => _tray?.ApplyUiLanguage(language);
            mainWindow.SettingsChanged += (_, _) =>
            {
                _tray?.SetEnabled(mainWindow.IsSelectionTranslationEnabled);
                _tray?.ApplyUiLanguage(mainWindow.SavedSettings.UiLanguage);
            };
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, _) => _allowWindowClose = true;
            mainWindow.Closing += (_, args) =>
            {
                if (_allowWindowClose || _tray is null) return;
                args.Cancel = true;
                mainWindow.Hide();
            };
            try
            {
                _tray = new YitaTrayController(
                    ShowMainWindow,
                    () =>
                    {
                        mainWindow.ToggleEnabledFromTray();
                        _tray?.SetEnabled(mainWindow.IsSelectionTranslationEnabled);
                    },
                    () =>
                    {
                        _allowWindowClose = true;
                        desktop.Shutdown(0);
                    },
                    mainWindow.IsSelectionTranslationEnabled, _selectionRuntime,
                    mainWindow.CopyDiagnostics, mainWindow.ShowAbout,
                    translateClipboard: mainWindow.TranslateClipboardFromTray,
                    createStatusIcon: platform.CreateStatusIcon);
            }
            catch
            {
                // A desktop environment may not provide a tray host. The
                // main window and native selection layer remain usable.
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
            _tray?.ApplyUiLanguage(mainWindow.SavedSettings.UiLanguage);
            if (_tray is not null && desktop.Args?.Contains("--background", StringComparer.OrdinalIgnoreCase) == true)
            {
                desktop.MainWindow = null;
                _ = mainWindow.Initialization;
            }
            _singleInstance?.StartActivationListener(() => Avalonia.Threading.Dispatcher.UIThread.Post(ShowMainWindow));
            desktop.Exit += (_, _) =>
            {
                _displayPreferencesTimer?.Stop();
                mainWindow.ShutdownServices();
                _tray?.Dispose();
                _tray = null;
                _selectionRuntime?.Dispose();
                _singleInstance?.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void RefreshDisplayPreferences()
    {
        try
        {
            var enabled = DesktopDisplayPreferences.AnimationsEnabled;
            if (enabled == ReferenceMotion.SystemAnimationsEnabled) return;
            ReferenceMotion.SetSystemAnimations(enabled);
            ReferenceTheme.ApplyMotion(Resources);
            _settingsWindow?.RefreshMotionResources();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Display preference check failed: {0}", exception.GetType().Name);
        }
    }

    private void ShowMainWindow()
    {
        if (_allowWindowClose || _settingsWindow is not { } window) return;
        if (!window.IsVisible) window.Show();
        window.Activate();
    }
}
