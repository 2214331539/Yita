using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Yita.Native.Windows;

namespace Yita.Desktop;

public sealed class App : Application
{
    private WindowsSelectionRuntime? _windowsRuntime;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            desktop.Exit += (_, _) => _windowsRuntime?.Dispose();
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    _windowsRuntime = new WindowsSelectionRuntime();
                    _windowsRuntime.SelectionCaptured += (_, args) =>
                        Avalonia.Threading.Dispatcher.UIThread.Post(
                            async () => await mainWindow.ShowSelectionTranslationAsync(args.Request, args.Result));
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
        }
        base.OnFrameworkInitializationCompleted();
    }
}
