using System.Threading;

namespace Yita.Native.Windows;

/// <summary>Prevents two cross-platform Yita shells from owning the global hooks.</summary>
public sealed class WindowsSingleInstanceGuard : IDisposable
{
    private Mutex? _mutex;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _activationWait;
    private bool _ownsMutex;
    private volatile bool _disposed;

    public WindowsSingleInstanceGuard(bool requestActivation = true) : this("Yita.CrossPlatform", requestActivation) { }

    internal WindowsSingleInstanceGuard(string identity, bool requestActivation = true)
    {
        if (!OperatingSystem.IsWindows())
        {
            IsOwner = true;
            return;
        }

        try
        {
            _mutex = new Mutex(initiallyOwned: true, "Local\\" + identity + ".SingleInstance", out var createdNew);
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
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + identity + ".OpenSettings");
        if (!IsOwner && requestActivation) _activation.Set();
    }

    public bool IsOwner { get; }

    public void StartActivationListener(Action openSettings)
    {
        if (_disposed || !IsOwner || _activation is null || _activationWait is not null) return;
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) =>
        {
            if (_disposed) return;
            try { openSettings(); } catch { }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activationWait?.Unregister(null);
        _activation?.Dispose();
        _activation = null;
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _mutex?.Dispose();
        _mutex = null;
    }
}
