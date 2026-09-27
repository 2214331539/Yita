using System.Threading;

namespace Yita.Native.Windows;

/// <summary>Prevents two cross-platform Yita shells from owning the global hooks.</summary>
public sealed class WindowsSingleInstanceGuard : IDisposable
{
    private const string MutexName = "Local\\Yita.CrossPlatform.SingleInstance";
    private Mutex? _mutex;
    private bool _ownsMutex;
    private bool _disposed;

    public WindowsSingleInstanceGuard()
    {
        if (!OperatingSystem.IsWindows())
        {
            IsOwner = true;
            return;
        }

        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            _ownsMutex = createdNew;
            IsOwner = createdNew;
        }
        catch (AbandonedMutexException)
        {
            // The previous process terminated without releasing the mutex. The
            // OS has already transferred ownership to this process.
            _ownsMutex = true;
            IsOwner = true;
        }
    }

    public bool IsOwner { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _mutex?.Dispose();
        _mutex = null;
    }
}
