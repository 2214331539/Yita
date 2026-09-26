using Yita.Services;

namespace Yita.Tests;

public sealed class QuestionAnswerRequestGateTests
{
    [Fact]
    public void Begin_ForSameSession_CancelsAndSupersedesPreviousRequest()
    {
        using var gate = new QuestionAnswerRequestGate();
        var sessionId = Guid.NewGuid();
        var first = gate.Begin(sessionId);

        var second = gate.Begin(sessionId);

        Assert.True(first.Cancellation.IsCancellationRequested);
        Assert.False(gate.IsCurrent(first));
        Assert.True(gate.IsCurrent(second));
        Assert.True(second.Version > first.Version);
    }

    [Fact]
    public void Requests_ForDifferentSessions_RemainIndependent()
    {
        using var gate = new QuestionAnswerRequestGate();
        var first = gate.Begin(Guid.NewGuid());
        var second = gate.Begin(Guid.NewGuid());

        gate.Cancel(first.SessionId);

        Assert.True(first.Cancellation.IsCancellationRequested);
        Assert.True(gate.IsCurrent(second));
    }

    [Fact]
    public void Complete_RejectsLateUpdates()
    {
        using var gate = new QuestionAnswerRequestGate();
        var lease = gate.Begin(Guid.NewGuid());

        gate.Complete(lease);

        Assert.False(gate.IsCurrent(lease));
    }
}
