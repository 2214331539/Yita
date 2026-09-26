using System.Diagnostics;
using System.Security.Principal;

namespace Yita.Services;

/// <summary>
/// Keeps one application instance alive per Windows user session and lets later
/// instances ask the primary instance to activate itself.
/// </summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string KernelObjectNamespace = @"Local\";
    private readonly object _syncRoot = new();
    private readonly Mutex _lifetimeMutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly EventWaitHandle _activationAcknowledgedEvent;
    private readonly EventWaitHandle _stopEvent = new(false, EventResetMode.ManualReset);
    private Thread? _listenerThread;
    private Action? _activationCallback;
    private bool _disposed;

    private SingleInstanceCoordinator(
        Mutex lifetimeMutex,
        EventWaitHandle activationEvent,
        EventWaitHandle activationAcknowledgedEvent,
        bool isPrimary)
    {
        _lifetimeMutex = lifetimeMutex;
        _activationEvent = activationEvent;
        _activationAcknowledgedEvent = activationAcknowledgedEvent;
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }

    public bool WasActivationAcknowledged { get; private set; }

    /// <summary>
    /// Acquires the lifetime slot for <paramref name="instanceName"/>. A later
    /// instance signals the primary instance before this method returns.
    /// </summary>
    public static SingleInstanceCoordinator Acquire(string? instanceName = null)
    {
        return AcquireCore(instanceName, signalPrimary: true);
    }

    /// <summary>
    /// Handles the narrow race where a user relaunches while the previous
    /// primary process is still releasing its kernel objects. A live primary is
    /// still activated immediately; if it disappears during the short grace
    /// period, the relaunch takes over instead of leaving no running instance.
    /// </summary>
    public static SingleInstanceCoordinator AcquireWithTakeoverRetry(
        string? instanceName = null,
        int retryCount = 30,
        TimeSpan? retryDelay = null)
    {
        if (retryCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryCount));
        }

        var delay = retryDelay ?? TimeSpan.FromMilliseconds(120);
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        }

        for (var attempt = 0; ; attempt++)
        {
            var coordinator = AcquireCore(
                instanceName,
                signalPrimary: attempt == 0);
            if (coordinator.IsPrimary)
            {
                return coordinator;
            }

            if (coordinator.WaitForActivationAcknowledgement(delay))
            {
                return coordinator;
            }

            if (attempt >= retryCount)
            {
                return coordinator;
            }

            coordinator.Dispose();
        }
    }

    private static SingleInstanceCoordinator AcquireCore(
        string? instanceName,
        bool signalPrimary)
    {
        var normalizedName = NormalizeInstanceName(instanceName ?? CreateDefaultInstanceName());
        var activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            $"{KernelObjectNamespace}{normalizedName}.Activate");
        var activationAcknowledgedEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            $"{KernelObjectNamespace}{normalizedName}.Activated");

        Mutex? lifetimeMutex = null;
        try
        {
            // The mutex handle itself is the lifetime claim. Avoiding mutex ownership
            // makes disposal safe even when application shutdown changes threads.
            lifetimeMutex = new Mutex(
                false,
                $"{KernelObjectNamespace}{normalizedName}.Lifetime",
                out var createdNew);

            var coordinator = new SingleInstanceCoordinator(
                lifetimeMutex,
                activationEvent,
                activationAcknowledgedEvent,
                createdNew);

            if (!createdNew && signalPrimary)
            {
                activationEvent.Set();
            }

            return coordinator;
        }
        catch
        {
            lifetimeMutex?.Dispose();
            activationEvent.Dispose();
            activationAcknowledgedEvent.Dispose();
            throw;
        }
    }

    private bool WaitForActivationAcknowledgement(TimeSpan timeout)
    {
        if (IsPrimary)
        {
            return false;
        }

        WasActivationAcknowledged = _activationAcknowledgedEvent.WaitOne(timeout);
        return WasActivationAcknowledged;
    }

    /// <summary>
    /// Starts the primary instance's background activation listener. The
    /// callback runs on the listener thread and must return promptly; it should
    /// enqueue any WPF work onto the UI dispatcher.
    /// </summary>
    public void StartListening(Action activationCallback)
    {
        ArgumentNullException.ThrowIfNull(activationCallback);

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!IsPrimary)
            {
                throw new InvalidOperationException(
                    "Only the primary application instance can listen for activation requests.");
            }

            if (_listenerThread is not null)
            {
                throw new InvalidOperationException("The activation listener has already started.");
            }

            _activationCallback = activationCallback;
            _listenerThread = new Thread(ListenForActivation)
            {
                IsBackground = true,
                Name = "Yita.SingleInstanceListener"
            };
            _listenerThread.Start();
        }
    }

    public void Dispose()
    {
        Thread? listenerThread;

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activationCallback = null;
            listenerThread = _listenerThread;
            _stopEvent.Set();
        }

        if (listenerThread is not null && listenerThread != Thread.CurrentThread)
        {
            listenerThread.Join();
        }

        _stopEvent.Dispose();
        _activationEvent.Dispose();
        _activationAcknowledgedEvent.Dispose();
        _lifetimeMutex.Dispose();
    }

    internal static string CreateDefaultInstanceName()
    {
        var userScope = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(userScope))
        {
            userScope = $"{Environment.UserDomainName}.{Environment.UserName}";
        }

        return $"Yita.{userScope}.Session{Process.GetCurrentProcess().SessionId}";
    }

    private void ListenForActivation()
    {
        var waitHandles = new WaitHandle[] { _stopEvent, _activationEvent };

        while (WaitHandle.WaitAny(waitHandles) == 1)
        {
            _activationAcknowledgedEvent.Set();
            Action? callback;
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                callback = _activationCallback;
            }

            if (callback is not null)
            {
                try
                {
                    callback.Invoke();
                }
                catch
                {
                    // A UI activation failure must not terminate the listener.
                }
            }
        }
    }

    private static string NormalizeInstanceName(string instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            throw new ArgumentException("An instance name is required.", nameof(instanceName));
        }

        var normalizedName = instanceName.Trim();
        if (normalizedName.Contains('\\'))
        {
            throw new ArgumentException(
                "The instance name cannot contain a namespace separator.",
                nameof(instanceName));
        }

        return normalizedName;
    }
}
