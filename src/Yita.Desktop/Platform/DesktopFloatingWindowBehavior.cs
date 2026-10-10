using Avalonia.Controls;
using Avalonia.Threading;
using Yita.Native.Mac;

namespace Yita.Desktop;

internal static class DesktopFloatingWindowBehavior
{
    internal static void Apply(Window window)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Dispatcher.UIThread.VerifyAccess();
        var handle = window.TryGetPlatformHandle();
        if (handle is not null) MacPopupWindowBehavior.Apply(handle.Handle, handle.HandleDescriptor);
    }

    internal static void Restore(Window window)
    {
        var area = window.Screens.ScreenFromPoint(window.Position)?.WorkingArea ?? window.Screens.Primary?.WorkingArea;
        if (area is { } bounds)
        {
            var scale = window.Screens.ScreenFromPoint(window.Position)?.Scaling ?? window.Screens.Primary?.Scaling ?? 1;
            window.Position = new((int)Math.Clamp(window.Position.X, bounds.X, Math.Max(bounds.X, bounds.Right - window.Width * scale)),
                (int)Math.Clamp(window.Position.Y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - window.Height * scale)));
        }
        var activate = window.ShowActivated;
        window.ShowActivated = false;
        try { Apply(window); window.Show(); }
        finally { window.ShowActivated = activate; }
    }
}
