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

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
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
                mainWindow.ShutdownServices();
                _tray?.Dispose();
                _tray = null;
                _selectionRuntime?.Dispose();
                _singleInstance?.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ShowMainWindow()
    {
        if (_allowWindowClose || _settingsWindow is not { } window) return;
        if (!window.IsVisible) window.Show();
        window.Activate();
    }
}
