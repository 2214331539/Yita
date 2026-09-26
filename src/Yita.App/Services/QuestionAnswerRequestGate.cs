namespace Yita.Services;

internal sealed class QuestionAnswerRequestGate : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, QuestionAnswerRequestLease> _leases = new();
    private long _nextVersion;
    private bool _disposed;

    internal QuestionAnswerRequestLease Begin(Guid sessionId)
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_leases.Remove(sessionId, out var previous))
            {
                previous.Cancellation.Cancel();
            }

            var lease = new QuestionAnswerRequestLease(
                sessionId,
                ++_nextVersion,
                new CancellationTokenSource());
            _leases.Add(sessionId, lease);
            return lease;
        }
    }

    internal bool IsCurrent(QuestionAnswerRequestLease lease)
    {
        lock (_syncRoot)
        {
            return !_disposed
                   && _leases.TryGetValue(lease.SessionId, out var current)
                   && ReferenceEquals(current, lease)
                   && !lease.Cancellation.IsCancellationRequested;
        }
    }

    internal void Cancel(Guid sessionId)
    {
        lock (_syncRoot)
        {
            if (_leases.Remove(sessionId, out var lease))
            {
                lease.Cancellation.Cancel();
            }
        }
    }

    internal void Complete(QuestionAnswerRequestLease lease)
    {
        lock (_syncRoot)
        {
            if (_leases.TryGetValue(lease.SessionId, out var current)
                && ReferenceEquals(current, lease))
            {
                _leases.Remove(lease.SessionId);
            }
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var lease in _leases.Values)
            {
                lease.Cancellation.Cancel();
            }

            _leases.Clear();
        }
    }
}

internal sealed record QuestionAnswerRequestLease(
    Guid SessionId,
    long Version,
    CancellationTokenSource Cancellation);
