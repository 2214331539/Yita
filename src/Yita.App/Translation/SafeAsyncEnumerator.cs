using System.Diagnostics;

namespace Yita.Translation;

/// <summary>
/// Single-consumer iterator lease. A timed-out MoveNext must finish before the
/// iterator is disposed or its physical concurrency slot can be reused.
/// </summary>
internal sealed class SafeAsyncEnumerator<T>(IAsyncEnumerator<T> inner, Action release) : IAsyncEnumerator<T>
{
    private Task<bool>? _pendingMove;
    private int _disposed;
    internal Task CleanupCompletion { get; private set; } = Task.CompletedTask;
    public T Current => inner.Current;

    public ValueTask<bool> MoveNextAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_pendingMove is { IsCompleted: false })
            throw new InvalidOperationException("An iterator move is already pending.");
        _pendingMove = inner.MoveNextAsync().AsTask();
        return new ValueTask<bool>(_pendingMove);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        var pending = _pendingMove;
        var deferred = pending is { IsCompleted: false };
        CleanupCompletion = CleanupAsync(pending);
        if (!deferred) return new ValueTask(CleanupCompletion);
        _ = ObserveDeferredCleanupAsync(CleanupCompletion);
        return ValueTask.CompletedTask;
    }

    private async Task CleanupAsync(Task<bool>? pending)
    {
        try
        {
            if (pending is not null)
            {
                try { await pending.ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // The caller owns the move's failure. Observe late faults
                    // without publishing their content or masking cancellation.
                }
            }
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        finally { release(); }
    }

    private static async Task ObserveDeferredCleanupAsync(Task cleanup)
    {
        try { await cleanup.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Debug.WriteLine($"Yita stream cleanup failed: {exception.GetType().Name}");
        }
    }
}
