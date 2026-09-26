using Yita.Services;

namespace Yita.Tests;

public sealed class RequestVersionGateTests
{
    [Fact]
    public void NewRequest_CancelsAndInvalidatesPreviousRequest()
    {
        using var gate = new RequestVersionGate();
        var first = gate.BeginRequest();

        var second = gate.BeginRequest();

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(gate.IsCurrent(first.Version));
        Assert.True(gate.IsCurrent(second.Version));
        Assert.False(second.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void CancelActive_InvalidatesCurrentRequest()
    {
        using var gate = new RequestVersionGate();
        var lease = gate.BeginRequest();

        gate.CancelActive();

        Assert.True(lease.CancellationToken.IsCancellationRequested);
        Assert.False(gate.IsCurrent(lease.Version));
    }

    [Fact]
    public void TryDetach_PreservesRequestWhenANewTransientRequestBegins()
    {
        using var gate = new RequestVersionGate();
        var pinned = gate.BeginRequest();

        Assert.True(gate.TryDetach(pinned.Version, out var detachedCancellation));
        var next = gate.BeginRequest();

        Assert.NotNull(detachedCancellation);
        Assert.False(pinned.CancellationToken.IsCancellationRequested);
        Assert.True(gate.IsCurrent(next.Version));
        detachedCancellation!.Cancel();
        detachedCancellation.Dispose();
        Assert.True(pinned.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void CancelIfCurrent_DoesNotCancelDifferentRequest()
    {
        using var gate = new RequestVersionGate();
        var current = gate.BeginRequest();

        Assert.False(gate.CancelIfCurrent(current.Version + 1));
        Assert.False(current.CancellationToken.IsCancellationRequested);
        Assert.True(gate.CancelIfCurrent(current.Version));
        Assert.True(current.CancellationToken.IsCancellationRequested);
    }
}
