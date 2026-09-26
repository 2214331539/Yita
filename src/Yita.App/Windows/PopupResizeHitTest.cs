using Yita.Interop;
using Yita.Models;

namespace Yita.Windows;

internal static class PopupResizeHitTest
{
    internal static int ResolveWithInsetTop(
        NativeMethods.NativeRect windowBounds,
        int insetTop,
        ScreenPoint point,
        int edgeThickness)
    {
        if (insetTop > windowBounds.Top && insetTop < windowBounds.Bottom)
        {
            var contentBounds = new NativeMethods.NativeRect
            {
                Left = windowBounds.Left,
                Top = insetTop,
                Right = windowBounds.Right,
                Bottom = windowBounds.Bottom,
            };
            var contentHit = Resolve(contentBounds, point, edgeThickness);
            if (contentHit is NativeMethods.HtTop
                or NativeMethods.HtTopLeft
                or NativeMethods.HtTopRight)
            {
                return contentHit;
            }
        }

        return Resolve(windowBounds, point, edgeThickness) switch
        {
            NativeMethods.HtTop => NativeMethods.HtClient,
            NativeMethods.HtTopLeft => NativeMethods.HtLeft,
            NativeMethods.HtTopRight => NativeMethods.HtRight,
            var hitTest => hitTest,
        };
    }

    internal static int Resolve(
        NativeMethods.NativeRect bounds,
        ScreenPoint point,
        int edgeThickness)
    {
        if (edgeThickness <= 0 || !bounds.Contains(point))
        {
            return 0;
        }

        var isLeft = point.X < bounds.Left + edgeThickness;
        var isRight = point.X >= bounds.Right - edgeThickness;
        var isTop = point.Y < bounds.Top + edgeThickness;
        var isBottom = point.Y >= bounds.Bottom - edgeThickness;

        if (isTop && isLeft)
        {
            return NativeMethods.HtTopLeft;
        }

        if (isTop && isRight)
        {
            return NativeMethods.HtTopRight;
        }

        if (isBottom && isLeft)
        {
            return NativeMethods.HtBottomLeft;
        }

        if (isBottom && isRight)
        {
            return NativeMethods.HtBottomRight;
        }

        if (isLeft)
        {
            return NativeMethods.HtLeft;
        }

        if (isRight)
        {
            return NativeMethods.HtRight;
        }

        if (isTop)
        {
            return NativeMethods.HtTop;
        }

        return isBottom ? NativeMethods.HtBottom : 0;
    }

    internal static ScreenPoint DecodeScreenPoint(IntPtr packedPoint)
    {
        var value = packedPoint.ToInt64();
        return new ScreenPoint(
            unchecked((short)(value & 0xffff)),
            unchecked((short)((value >> 16) & 0xffff)));
    }
}
