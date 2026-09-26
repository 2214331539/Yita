namespace Yita.Services;

/// <summary>
/// Keeps the latest AI explanation request for each popup. Replacing or
/// dismissing a request cancels it immediately; the running operation owns the
/// eventual disposal of its token source after it has observed cancellation.
/// </summary>
internal sealed class ExplanationRequestGate : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<long, ExplanationRequestLease> _leases = new();
    private long _nextVersion;
    private bool _disposed;

    public ExplanationRequestLease Begin(long requestId)
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_leases.Remove(requestId, out var previous))
            {
                previous.Cancellation.Cancel();
            }

            var lease = new ExplanationRequestLease(
                requestId,
                ++_nextVersion,
                new CancellationTokenSource());
            _leases.Add(requestId, lease);
            return lease;
        }
    }

    public bool IsCurrent(ExplanationRequestLease lease)
    {
        lock (_syncRoot)
        {
            return !_disposed
                   && _leases.TryGetValue(lease.RequestId, out var current)
                   && ReferenceEquals(current, lease)
                   && !lease.Cancellation.IsCancellationRequested;
        }
    }

    public void Cancel(long requestId)
    {
        lock (_syncRoot)
        {
            if (_leases.Remove(requestId, out var lease))
            {
                lease.Cancellation.Cancel();
            }
        }
    }

    public void Complete(ExplanationRequestLease lease)
    {
        lock (_syncRoot)
        {
            if (_leases.TryGetValue(lease.RequestId, out var current)
                && ReferenceEquals(current, lease))
            {
                _leases.Remove(lease.RequestId);
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

internal sealed record ExplanationRequestLease(
    long RequestId,
    long Version,
    CancellationTokenSource Cancellation);
