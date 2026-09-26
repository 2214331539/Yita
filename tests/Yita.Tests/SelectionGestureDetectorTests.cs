using Yita.Hooks;
using Yita.Models;

namespace Yita.Tests;

public sealed class SelectionGestureDetectorTests
{
    [Fact]
    public void Release_WithoutPress_DoesNotCreateGesture()
    {
        var detector = new SelectionGestureDetector(4, 4);

        var gesture = detector.Release(new ScreenPoint(20, 20), DateTimeOffset.UtcNow);

        Assert.Null(gesture);
    }

    [Fact]
    public void SmallMovement_IsTreatedAsClick()
    {
        var detector = new SelectionGestureDetector(4, 4);
        detector.Press(new ScreenPoint(10, 10));

        var gesture = detector.Release(new ScreenPoint(13, 13), DateTimeOffset.UtcNow);

        Assert.Null(gesture);
    }

    [Fact]
    public void HorizontalDrag_CreatesSingleGesture()
    {
        var detector = new SelectionGestureDetector(4, 4);
        detector.Press(new ScreenPoint(10, 10));

        var gesture = detector.Release(new ScreenPoint(20, 11), DateTimeOffset.UtcNow);
        var secondRelease = detector.Release(new ScreenPoint(30, 11), DateTimeOffset.UtcNow);

        Assert.NotNull(gesture);
        Assert.Equal(new ScreenPoint(10, 10), gesture.Value.Start);
        Assert.Equal(new ScreenPoint(20, 11), gesture.Value.End);
        Assert.Null(secondRelease);
    }
}
