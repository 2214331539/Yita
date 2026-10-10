using Avalonia.Threading;
using Yita.Native.Mac;
using Yita.Native.Windows;

namespace Yita.Desktop;

internal static class DesktopDisplayPreferences
{
    internal static bool AnimationsEnabled
    {
        get
        {
            Dispatcher.UIThread.VerifyAccess();
            return OperatingSystem.IsWindows() ? WindowsDisplayPreferences.AnimationsEnabled
                : !OperatingSystem.IsMacOS() || !MacDisplayPreferences.ReduceMotion;
        }
    }
}
