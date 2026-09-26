using Yita.Translation;

namespace Yita.Tests;

public sealed class SafeAsyncEnumeratorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbandonedMoveRetainsSlotUntilMoveAndDisposeFinish(bool lateFault)
    {
        var inner = new ControlledEnumerator();
        var releases = 0;
        var lease = new SafeAsyncEnumerator<int>(inner, () => releases++);
        var move = lease.MoveNextAsync().AsTask();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move.WaitAsync(cancellation.Token));
        Assert.True(lease.DisposeAsync().IsCompletedSuccessfully);
        Assert.False(inner.DisposeStarted.Task.IsCompleted);
        Assert.Equal(0, releases);
        if (lateFault) inner.Move.SetException(new InvalidOperationException("late failure"));
        else inner.Move.SetResult(true);
        await inner.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, releases);
        inner.DisposeGate.SetResult();
        await lease.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await lease.DisposeAsync();
        Assert.Equal(1, releases);
        Assert.Equal(1, inner.DisposeCalls);
    }

    [Fact]
    public async Task TimeoutIsNotMaskedByLateCleanupFailure()
    {
        var inner = new ControlledEnumerator();
        var releases = 0;
        var lease = new SafeAsyncEnumerator<int>(inner, () => releases++);
        var move = lease.MoveNextAsync().AsTask();
        await Assert.ThrowsAsync<TimeoutException>(() => move.WaitAsync(TimeSpan.Zero));
        await lease.DisposeAsync();
        inner.Move.SetResult(false);
        await inner.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        inner.DisposeGate.SetException(new InvalidOperationException("cleanup failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.CleanupCompletion);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task NormalCompletionAndRepeatedDisposalReleaseExactlyOnce()
    {
        var inner = new ControlledEnumerator();
        inner.Move.SetResult(false);
        inner.DisposeGate.SetResult();
        var releases = 0;
        var lease = new SafeAsyncEnumerator<int>(inner, () => releases++);
        Assert.False(await lease.MoveNextAsync());
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.Equal(1, releases);
        Assert.Equal(1, inner.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => lease.MoveNextAsync());
    }

    private sealed class ControlledEnumerator : IAsyncEnumerator<int>
    {
        internal readonly TaskCompletionSource<bool> Move = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisposeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisposeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCalls;
        public int Current => 42;
        public ValueTask<bool> MoveNextAsync() => new(Move.Task);
        public ValueTask DisposeAsync()
        {
            Assert.True(Move.Task.IsCompleted, "Dispose raced an in-flight move.");
            DisposeCalls++;
            DisposeStarted.SetResult();
            return new ValueTask(DisposeGate.Task);
        }
    }
}
