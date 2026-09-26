using Yita.Interop;
using Yita.Models;

namespace Yita.Tests;

public sealed class NativeRectTests
{
    [Theory]
    [InlineData(100, 200, true)]
    [InlineData(399, 499, true)]
    [InlineData(400, 499, false)]
    [InlineData(399, 500, false)]
    [InlineData(99, 200, false)]
    public void Contains_UsesWin32ExclusiveRightAndBottomEdges(int x, int y, bool expected)
    {
        var rectangle = new NativeMethods.NativeRect
        {
            Left = 100,
            Top = 200,
            Right = 400,
            Bottom = 500,
        };

        Assert.Equal(expected, rectangle.Contains(new ScreenPoint(x, y)));
    }
}
