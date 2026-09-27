using System.Drawing;
using Yita.Models;

namespace Yita.Windows;

internal sealed record PopupPlacement(double OffsetX, double OffsetY)
{
    internal static PopupPlacement FromPosition(ScreenPoint anchor, Point position, double dpiX, double dpiY) =>
        new((position.X - anchor.X) / dpiX, (position.Y - anchor.Y) / dpiY);
    internal bool IsValid => double.IsFinite(OffsetX) && double.IsFinite(OffsetY)
        && Math.Abs(OffsetX) <= 32768 && Math.Abs(OffsetY) <= 32768;

    internal static Point Resolve(ScreenPoint anchor, Size size, Rectangle workArea,
        double dpiX, double dpiY, PopupPlacement? preference)
    {
        const int margin = 8;
        var x = anchor.X + 10;
        var y = anchor.Y + 12;
        if (preference is { IsValid: true })
        {
            x = anchor.X + (int)Math.Round(preference.OffsetX * dpiX);
            y = anchor.Y + (int)Math.Round(preference.OffsetY * dpiY);
        }
        else
        {
            if (x + size.Width > workArea.Right - margin) x = anchor.X - size.Width - 10;
            if (y + size.Height > workArea.Bottom - margin) y = anchor.Y - size.Height - 12;
        }
        return new Point(
            Math.Clamp(x, workArea.Left + margin, Math.Max(workArea.Left + margin, workArea.Right - size.Width - margin)),
            Math.Clamp(y, workArea.Top + margin, Math.Max(workArea.Top + margin, workArea.Bottom - size.Height - margin)));
    }
}

internal sealed class PopupPlacementState
{
    internal ScreenPoint Anchor { get; private set; }
    internal bool UserMoved { get; private set; }
    internal PopupPlacement? Preference { get; set; }

    internal bool SetAnchor(ScreenPoint anchor, bool pinned)
    {
        if (Anchor == anchor || pinned) return false;
        Anchor = anchor;
        UserMoved = false;
        return true;
    }

    internal void MovedTo(Point position, double dpiX, double dpiY)
    {
        Preference = PopupPlacement.FromPosition(Anchor, position, dpiX, dpiY);
        UserMoved = true;
    }
}
