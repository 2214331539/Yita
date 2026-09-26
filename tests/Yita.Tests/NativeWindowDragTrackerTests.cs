using Yita.Hooks;
using Yita.Interop;

namespace Yita.Tests;

public sealed class NativeWindowDragTrackerTests
{
    [Theory]
    [InlineData(NativeMethods.HtCaption, false)]
    [InlineData(NativeMethods.HtSize, true)]
    [InlineData(NativeMethods.HtHScroll, true)]
    [InlineData(NativeMethods.HtVScroll, true)]
    [InlineData(NativeMethods.HtLeft, true)]
    [InlineData(NativeMethods.HtBorder, true)]
    [InlineData(1, false)]
    [InlineData(20, false)]
    public void IsDragHitTest_ClassifiesWindowDragRegions(int hitTest, bool expected)
    {
        Assert.Equal(expected, NativeWindowDragTracker.IsDragHitTest(hitTest));
    }

    [Fact]
    public void HaveBoundsChanged_ReturnsFalseForIdenticalRectangle()
    {
        var bounds = CreateBounds(10, 20, 300, 200);

        Assert.False(NativeWindowDragTracker.HaveBoundsChanged(bounds, bounds));
    }

    [Theory]
    [InlineData(11, 20, 300, 200)]
    [InlineData(10, 21, 300, 200)]
    [InlineData(10, 20, 301, 200)]
    [InlineData(10, 20, 300, 201)]
    public void HaveBoundsChanged_DetectsMoveOrResize(int left, int top, int width, int height)
    {
        var initial = CreateBounds(10, 20, 300, 200);
        var current = CreateBounds(left, top, width, height);

        Assert.True(NativeWindowDragTracker.HaveBoundsChanged(initial, current));
    }

    private static NativeMethods.NativeRect CreateBounds(int left, int top, int width, int height)
    {
        return new NativeMethods.NativeRect
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height,
        };
    }
}
