using Yita.Selection;

namespace Yita.Tests;

public sealed class EdgeAccessibilityActivatorTests
{
    [Theory]
    [InlineData("msedge")]
    [InlineData("MSEdge")]
    public void RecognizesOnlyMicrosoftEdge(string processName)
    {
        Assert.True(EdgeAccessibilityActivator.ShouldActivateProcessName(processName));
        Assert.False(EdgeAccessibilityActivator.ShouldActivateProcessName("chrome"));
        Assert.False(EdgeAccessibilityActivator.ShouldActivateProcessName("zotero"));
        Assert.False(EdgeAccessibilityActivator.ShouldActivateProcessName(null));
    }

    [Fact]
    public void FailedReadCanRearmSameEdgeWindowAfterShortCooldown()
    {
        var gate = new EdgeAccessibilityRefreshGate();
        Assert.True(gate.TryEnter(12, new IntPtr(1), 1000, refresh: false));
        Assert.False(gate.TryEnter(12, new IntPtr(1), 1100, refresh: false));
        Assert.False(gate.TryEnter(12, new IntPtr(1), 1100, refresh: true));
        Assert.True(gate.TryEnter(12, new IntPtr(1), 1400, refresh: true));
    }

    [Fact]
    public void ActivationExpiresAndDoesNotTreatOneProcessAsPermanentlyReady()
    {
        var gate = new EdgeAccessibilityRefreshGate();
        Assert.True(gate.TryEnter(12, new IntPtr(1), 1000, refresh: false));
        Assert.True(gate.TryEnter(12, new IntPtr(1), 20_000, refresh: false));
    }

    [Fact]
    public void DifferentEdgeWindowsAndReusedHandlesDoNotShareActivationState()
    {
        var gate = new EdgeAccessibilityRefreshGate();
        Assert.True(gate.TryEnter(12, new IntPtr(1), 1000, refresh: false));
        Assert.True(gate.TryEnter(12, new IntPtr(2), 1000, refresh: false));
        Assert.True(gate.TryEnter(13, new IntPtr(1), 1000, refresh: false));
    }
}
