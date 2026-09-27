using System.Drawing;
using System.IO;
using Yita.Models;
using Yita.Settings;
using Yita.Windows;

namespace Yita.Tests;

public sealed class PopupPlacementTests
{
    [Fact]
    public void NewSelectionReleasesDragLockButPreservesRelativeOffset()
    {
        var state = new PopupPlacementState();
        state.SetAnchor(new ScreenPoint(300, 200), pinned: false);
        state.MovedTo(new Point(360, 240), 1, 1);
        Assert.True(state.UserMoved);
        // Streaming frames for the same selection must not move the window.
        Assert.False(state.SetAnchor(new ScreenPoint(300, 200), pinned: false));
        Assert.True(state.UserMoved);
        Assert.True(state.SetAnchor(new ScreenPoint(900, 500), pinned: false));
        Assert.False(state.UserMoved);
        var next = PopupPlacement.Resolve(state.Anchor, new Size(300, 200),
            new Rectangle(0, 0, 1920, 1080), 1, 1, state.Preference);
        Assert.Equal(new Point(960, 540), next);
    }

    [Fact]
    public void PinnedPopupKeepsItsAnchorAndDragPosition()
    {
        var state = new PopupPlacementState();
        state.SetAnchor(new ScreenPoint(300, 200), false);
        state.MovedTo(new Point(360, 240), 1, 1);
        Assert.False(state.SetAnchor(new ScreenPoint(900, 500), true));
        Assert.Equal(new ScreenPoint(300, 200), state.Anchor);
        Assert.True(state.UserMoved);
    }

    [Fact]
    public void ReversingDragKeepsTheSameRegionAnchor()
    {
        var start = new ScreenPoint(200, 300);
        var end = new ScreenPoint(600, 400);
        var forward = new SelectionGesture(start, end, DateTimeOffset.Now);
        var reverse = new SelectionGesture(end, start, DateTimeOffset.Now);
        Assert.Equal(new ScreenPoint(200, 400), forward.PopupAnchor);
        Assert.Equal(forward.PopupAnchor, reverse.PopupAnchor);
    }

    [Fact]
    public void ScreenClampDoesNotOverwriteRememberedOffset()
    {
        var state = new PopupPlacementState();
        state.SetAnchor(new ScreenPoint(300, 200), false);
        state.MovedTo(new Point(360, 240), 1, 1);
        state.SetAnchor(new ScreenPoint(1900, 1000), false);
        var work = new Rectangle(0, 0, 1920, 1080);
        var clamped = PopupPlacement.Resolve(state.Anchor, new Size(300, 200), work, 1, 1, state.Preference);
        Assert.Equal(new Point(1612, 872), clamped);
        state.SetAnchor(new ScreenPoint(500, 400), false);
        Assert.Equal(new Point(560, 440), PopupPlacement.Resolve(state.Anchor, new Size(300, 200), work, 1, 1, state.Preference));
    }

    [Fact]
    public void OffsetFollowsNewSelectionAndScalesWithDpi()
    {
        var preference = new PopupPlacement(-100, 40);
        var first = PopupPlacement.Resolve(new ScreenPoint(500, 300), new Size(300, 200),
            new Rectangle(0, 0, 1920, 1080), 1, 1, preference);
        var second = PopupPlacement.Resolve(new ScreenPoint(900, 500), new Size(300, 200),
            new Rectangle(0, 0, 1920, 1080), 1.5, 1.5, preference);
        Assert.Equal(new Point(400, 340), first);
        Assert.Equal(new Point(750, 560), second);
    }

    [Fact]
    public void OffsetIsClampedOnMonitorWithNegativeCoordinates()
    {
        var result = PopupPlacement.Resolve(new ScreenPoint(-100, 800), new Size(400, 300),
            new Rectangle(-1920, 0, 1920, 1040), 1, 1, new PopupPlacement(1000, 1000));
        Assert.Equal(new Point(-408, 732), result);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e10)]
    public void InvalidOffsetUsesDefaultPlacement(double offset)
    {
        var anchor = new ScreenPoint(600, 400);
        var screen = new Rectangle(0, 0, 1920, 1080);
        var size = new Size(300, 200);
        Assert.Equal(PopupPlacement.Resolve(anchor, size, screen, 1, 1, null),
            PopupPlacement.Resolve(anchor, size, screen, 1, 1, new PopupPlacement(offset, 0)));
    }

    [Fact]
    public void PlacementPersistsAcrossStoreInstancesAndCorruptionFallsBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Yita-placement-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "placement.json");
        try
        {
            var preference = new PopupPlacement(-80, 65);
            new PopupPlacementStore(path).Save(preference);
            Assert.Equal(preference, new PopupPlacementStore(path).Load());
            File.WriteAllText(path, "invalid json");
            Assert.Null(new PopupPlacementStore(path).Load());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
