using Avalonia;

namespace Yita.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => args is ["--package-check"]
        ? (OperatingSystem.IsWindows() ? WindowsPackageCheck.RunAsync() : MacPackageCheck.RunAsync()).GetAwaiter().GetResult()
        : BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
