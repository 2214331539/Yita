using Yita.Interop;
using Yita.Models;
using Yita.Windows;

namespace Yita.Tests;

public sealed class PopupResizeHitTestTests
{
    private static readonly NativeMethods.NativeRect Bounds = new()
    {
        Left = 100,
        Top = 200,
        Right = 500,
        Bottom = 450,
    };

    [Theory]
    [InlineData(100, 200, NativeMethods.HtTopLeft)]
    [InlineData(499, 200, NativeMethods.HtTopRight)]
    [InlineData(100, 449, NativeMethods.HtBottomLeft)]
    [InlineData(499, 449, NativeMethods.HtBottomRight)]
    [InlineData(100, 300, NativeMethods.HtLeft)]
    [InlineData(499, 300, NativeMethods.HtRight)]
    [InlineData(300, 200, NativeMethods.HtTop)]
    [InlineData(300, 449, NativeMethods.HtBottom)]
    [InlineData(300, 300, 0)]
    public void Resolve_ReturnsExpectedHitTest(int x, int y, int expected)
    {
        Assert.Equal(expected, PopupResizeHitTest.Resolve(Bounds, new ScreenPoint(x, y), 8));
    }

    [Theory]
    [InlineData(300, 200, NativeMethods.HtClient)]
    [InlineData(100, 200, NativeMethods.HtLeft)]
    [InlineData(499, 200, NativeMethods.HtRight)]
    [InlineData(300, 260, NativeMethods.HtTop)]
    [InlineData(100, 260, NativeMethods.HtTopLeft)]
    [InlineData(499, 260, NativeMethods.HtTopRight)]
    [InlineData(300, 449, NativeMethods.HtBottom)]
    public void ResolveWithInsetTop_MovesTopResizeEdgeBelowToolbar(int x, int y, int expected)
    {
        Assert.Equal(
            expected,
            PopupResizeHitTest.ResolveWithInsetTop(Bounds, insetTop: 260, new ScreenPoint(x, y), 8));
    }

    [Theory]
    [InlineData(-10, -20)]
    [InlineData(32000, 16000)]
    public void DecodeScreenPoint_PreservesSignedCoordinates(int x, int y)
    {
        var packed = unchecked((int)((ushort)x | ((uint)(ushort)y << 16)));

        Assert.Equal(new ScreenPoint(x, y), PopupResizeHitTest.DecodeScreenPoint(new IntPtr(packed)));
    }
}
