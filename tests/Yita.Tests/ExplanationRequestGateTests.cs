using Yita.Services;

namespace Yita.Tests;

public sealed class ExplanationRequestGateTests
{
    [Fact]
    public void NewRequestForSamePopup_CancelsAndInvalidatesPreviousRequest()
    {
        using var gate = new ExplanationRequestGate();
        var first = gate.Begin(42);

        var second = gate.Begin(42);

        Assert.True(first.Cancellation.IsCancellationRequested);
        Assert.False(gate.IsCurrent(first));
        Assert.True(gate.IsCurrent(second));
        Assert.True(second.Version > first.Version);
    }

    [Fact]
    public void Cancel_OnlyRemovesTheSpecifiedPopupRequest()
    {
        using var gate = new ExplanationRequestGate();
        var first = gate.Begin(1);
        var second = gate.Begin(2);

        gate.Cancel(1);

        Assert.True(first.Cancellation.IsCancellationRequested);
        Assert.False(gate.IsCurrent(first));
        Assert.True(gate.IsCurrent(second));
    }

    [Fact]
    public void CompletingStaleRequest_DoesNotRemoveNewerRequest()
    {
        using var gate = new ExplanationRequestGate();
        var first = gate.Begin(12);
        var second = gate.Begin(12);

        gate.Complete(first);

        Assert.True(gate.IsCurrent(second));
        gate.Complete(second);
        Assert.False(gate.IsCurrent(second));
    }

    [Fact]
    public void Dispose_CancelsAllActiveRequests()
    {
        var gate = new ExplanationRequestGate();
        var first = gate.Begin(1);
        var second = gate.Begin(2);

        gate.Dispose();

        Assert.True(first.Cancellation.IsCancellationRequested);
        Assert.True(second.Cancellation.IsCancellationRequested);
        Assert.False(gate.IsCurrent(first));
    }
}
