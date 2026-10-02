using Yita.Core.Platform;
using Yita.Core.Settings;
using Yita.Native.Mac;
using Yita.Native.Windows;
using Yita.Translation;

namespace Yita.Desktop;

internal sealed class DesktopPlatformServices
{
    internal string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yita");
    internal IStartupRegistration Startup { get; } = OperatingSystem.IsWindows()
        ? new WindowsStartupService() : new UnsupportedStartupRegistration();

    internal ISingleInstanceGuard AcquireInstance(bool requestActivation) => OperatingSystem.IsWindows()
        ? new WindowsSingleInstanceGuard(requestActivation)
        : new PortableSingleInstanceGuard(Path.Combine(DataDirectory, "desktop-instance.lock"), requestActivation);

    internal ISelectionRuntime? CreateSelectionRuntime() => OperatingSystem.IsWindows()
        ? new WindowsSelectionRuntime() : null;

    internal IStatusIcon? CreateStatusIcon(string iconPath) => OperatingSystem.IsWindows()
        ? new WindowsTrayIcon(iconPath) : null;

    internal ISecretStore CreateSecretStore() => OperatingSystem.IsWindows()
        ? new WindowsCredentialSecretStore()
        : OperatingSystem.IsMacOS() ? new MacKeychainSecretStore() : new MemorySecretStore();

    internal ITranslationMemoryProtector? CreateMemoryProtector() => OperatingSystem.IsWindows()
        ? new WindowsTranslationMemoryProtector() : null;

    internal string PlatformName => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Desktop";
}
