namespace Yita.Core;

/// <summary>Invalidates earlier work even when a provider ignores cancellation.</summary>
public sealed class LatestRequestController : IDisposable
{
    private readonly object _gate = new();
    private RequestLease? _current;
    private bool _disposed;

    public RequestLease Begin()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _current?.Cancel();
            return _current = new RequestLease(this);
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _current?.Cancel();
            _current = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Cancel();
        }
    }

    public sealed class RequestLease : IDisposable
    {
        private readonly LatestRequestController _owner;
        private readonly CancellationTokenSource _cancellation = new();
        private bool _disposed;

        internal RequestLease(LatestRequestController owner) => _owner = owner;

        public CancellationToken Token => _cancellation.Token;

        public bool IsCurrent
        {
            get
            {
                lock (_owner._gate)
                    return !_disposed && ReferenceEquals(_owner._current, this)
                        && !_cancellation.IsCancellationRequested;
            }
        }

        internal void Cancel() => _cancellation.Cancel();

        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_disposed) return;
                if (ReferenceEquals(_owner._current, this)) _owner._current = null;
                _disposed = true;
                _cancellation.Dispose();
            }
        }
    }
}
