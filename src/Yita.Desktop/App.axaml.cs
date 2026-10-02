using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Yita.Native.Windows;

namespace Yita.Desktop;

public sealed class App : Application
{
    private WindowsSelectionRuntime? _windowsRuntime;
    private WindowsSingleInstanceGuard? _singleInstance;
    private YitaTrayController? _tray;
    private bool _allowWindowClose;
    private MainWindow? _settingsWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (OperatingSystem.IsWindows())
            {
                _singleInstance = new WindowsSingleInstanceGuard(requestActivation:
                    desktop.Args?.Contains("--background", StringComparer.OrdinalIgnoreCase) != true);
                if (!_singleInstance.IsOwner)
                {
                    desktop.Shutdown(0);
                    return;
                }

                try
                {
                    _windowsRuntime = new WindowsSelectionRuntime();
                    _windowsRuntime.Start();
                }
                catch
                {
                    // Another Yita instance may own Ctrl+Shift+T. The UI must
                    // still open so the user can configure or diagnose it.
                    _windowsRuntime?.Dispose();
                    _windowsRuntime = null;
                }
            }

            var mainWindow = new MainWindow(_windowsRuntime);
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
            if (_windowsRuntime is not null)
            {
                _windowsRuntime.ExternalPointerPressed += (_, _) =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(mainWindow.DismissUnpinnedWindows);
                _windowsRuntime.SelectionCaptured += (_, args) =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        async () =>
                        {
                            try { await mainWindow.ShowSelectionTranslationAsync(args.Request, args.Result); }
                            catch (Exception exception)
                            {
                                // Native events enter through async void; presentation failures
                                // must not escape onto the UI dispatcher or log selected text.
                                System.Diagnostics.Trace.TraceError("Selection presentation failed: {0}", exception.GetType().Name);
                            }
                        });
            }
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
                    mainWindow.IsSelectionTranslationEnabled, _windowsRuntime,
                    mainWindow.CopyDiagnostics, mainWindow.ShowAbout);
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
                _windowsRuntime?.Dispose();
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
