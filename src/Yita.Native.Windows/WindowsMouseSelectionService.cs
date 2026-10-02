using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Yita.Core.Selection;

namespace Yita.Native.Windows;

/// <summary>
/// Detects completed left-button drag gestures on a dedicated low-level hook
/// thread. The hook callback only copies the native payload into a channel;
/// hit testing and window-drag filtering happen off the callback thread.
/// </summary>
public sealed class WindowsMouseSelectionService : IDisposable
{
    public event EventHandler<ScreenPoint>? ExternalPointerPressed;
    private readonly object _lifecycleGate = new();
    private readonly WindowsNativeMethods.LowLevelMouseProc _hookCallback;
    private readonly SelectionGestureDetector _gestureDetector;
    private readonly Channel<MouseInput> _inputEvents;
    private readonly Task _inputProcessor;
    private Thread? _hookThread;
    private IntPtr _hookHandle;
    private uint _hookThreadId;
    private bool _stopping;
    private bool _disposed;
    private int _running;
    private int _generation;

    public WindowsMouseSelectionService()
    {
        _hookCallback = HookCallback;
        var minimumHorizontalDistance = OperatingSystem.IsWindows()
            ? WindowsNativeMethods.GetSystemMetrics(WindowsNativeMethods.SmCxDrag)
            : 4;
        var minimumVerticalDistance = OperatingSystem.IsWindows()
            ? WindowsNativeMethods.GetSystemMetrics(WindowsNativeMethods.SmCyDrag)
            : 4;
        _gestureDetector = new SelectionGestureDetector(
            minimumHorizontalDistance,
            minimumVerticalDistance);
        _inputEvents = Channel.CreateUnbounded<MouseInput>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
        _inputProcessor = Task.Run(ProcessInputEventsAsync);
    }

    public event EventHandler<SelectionGestureEventArgs>? SelectionGestureCompleted;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public void Start()
    {
        HookStartup startup;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hookThread is { IsAlive: true }) return;
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Windows mouse selection is unavailable.");

            _stopping = false;
            _hookHandle = IntPtr.Zero;
            _hookThreadId = 0;
            var generation = unchecked(++_generation);
            startup = new HookStartup();
            _hookThread = new Thread(() => HookThreadMain(startup, generation))
            {
                IsBackground = true,
                Name = "Yita.CrossPlatform.MouseHook",
            };
            _hookThread.Start();
        }

        if (!startup.Completion.Task.Wait(TimeSpan.FromSeconds(3)))
        {
            StopCurrentRun();
            throw new TimeoutException("全局鼠标钩子启动超时。");
        }

        if (startup.Exception is not null)
        {
            StopCurrentRun();
            throw new InvalidOperationException("无法安装全局鼠标钩子。", startup.Exception);
        }
    }

    internal void Rebind()
    {
        StopCurrentRun();
        Start();
    }

    private void HookThreadMain(HookStartup startup, int generation)
    {
        IntPtr hook = IntPtr.Zero;
        var installed = false;
        try
        {
            _ = WindowsNativeMethods.PeekMessage(
                out _, IntPtr.Zero, 0, 0, WindowsNativeMethods.PmNoRemove);
            hook = WindowsNativeMethods.SetWindowsHookEx(
                WindowsNativeMethods.WhMouseLl,
                _hookCallback,
                WindowsNativeMethods.GetModuleHandle(null),
                0);
            if (hook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            lock (_lifecycleGate)
            {
                if (_disposed || _stopping) throw new ObjectDisposedException(nameof(WindowsMouseSelectionService));
                _hookHandle = hook;
                _hookThreadId = WindowsNativeMethods.GetCurrentThreadId();
            }

            installed = true;
            Volatile.Write(ref _running, 1);
        }
        catch (Exception exception)
        {
            startup.Exception = exception;
        }
        finally
        {
            startup.Completion.TrySetResult();
        }

        if (!installed)
        {
            CleanupHook(hook);
            return;
        }

        try
        {
            while (WindowsNativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }
        }
        catch
        {
        }
        finally
        {
            CleanupHook(hook);
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        var message = unchecked((int)wParam.ToInt64());
        if (code >= 0 && message is WindowsNativeMethods.WmLButtonDown or WindowsNativeMethods.WmLButtonUp)
        {
            try
            {
                var data = Marshal.PtrToStructure<WindowsNativeMethods.MouseHookData>(lParam);
                if ((data.Flags & WindowsNativeMethods.LlMhfInjected) == 0)
                {
                    _inputEvents.Writer.TryWrite(new MouseInput(
                        data.Point,
                        message == WindowsNativeMethods.WmLButtonDown,
                        Volatile.Read(ref _generation),
                        DateTimeOffset.UtcNow));
                }
            }
            catch
            {
            }
        }

        return WindowsNativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private async Task ProcessInputEventsAsync()
    {
        var activeGeneration = 0;
        var activeWindow = IntPtr.Zero;
        WindowsNativeMethods.NativeRect initialBounds = default;
        var hasInitialBounds = false;

        await foreach (var input in _inputEvents.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_disposed || input.Generation != Volatile.Read(ref _generation) || !IsRunning) continue;
            try
            {
                if (input.Generation != activeGeneration)
                {
                    activeGeneration = input.Generation;
                    _gestureDetector.Cancel();
                    activeWindow = IntPtr.Zero;
                    hasInitialBounds = false;
                }

                if (input.IsPress)
                {
                    _gestureDetector.Press(input.Point.ToScreenPoint());
                    activeWindow = RootWindowAt(input.Point);
                    hasInitialBounds = activeWindow != IntPtr.Zero
                        && WindowsNativeMethods.GetWindowRect(activeWindow, out initialBounds);
                    if (activeWindow != IntPtr.Zero)
                    {
                        WindowsNativeMethods.GetWindowThreadProcessId(activeWindow, out var processId);
                        if (processId != 0 && processId != (uint)Environment.ProcessId)
                            ExternalPointerPressed?.Invoke(this, input.Point.ToScreenPoint());
                    }
                    continue;
                }

                var gesture = _gestureDetector.Release(input.Point.ToScreenPoint(), input.OccurredAt);
                var movedWindow = hasInitialBounds
                    && WindowsNativeMethods.GetWindowRect(activeWindow, out var currentBounds)
                    && HaveBoundsChanged(initialBounds, currentBounds);
                activeWindow = IntPtr.Zero;
                hasInitialBounds = false;

                if (movedWindow || gesture is not { } completed || !completed.IsFinite) continue;
                RaiseGesture(completed);
            }
            catch
            {
                _gestureDetector.Cancel();
                activeWindow = IntPtr.Zero;
                hasInitialBounds = false;
            }
        }
    }

    private void RaiseGesture(SelectionGesture gesture)
    {
        var handlers = SelectionGestureCompleted;
        if (handlers is null) return;
        var args = new SelectionGestureEventArgs(gesture);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<SelectionGestureEventArgs>>())
        {
            try { handler(this, args); }
            catch { }
        }
    }

    private void CleanupHook(IntPtr hook)
    {
        if (hook != IntPtr.Zero) WindowsNativeMethods.UnhookWindowsHookEx(hook);
        lock (_lifecycleGate)
        {
            if (ReferenceEquals(_hookThread, Thread.CurrentThread))
            {
                _hookThread = null;
                _hookHandle = IntPtr.Zero;
                _hookThreadId = 0;
            }
        }
        Volatile.Write(ref _running, 0);
    }

    private void StopCurrentRun()
    {
        Thread? thread;
        uint threadId;
        lock (_lifecycleGate)
        {
            _stopping = true;
            thread = _hookThread;
            threadId = _hookThreadId;
        }

        if (threadId != 0)
            WindowsNativeMethods.PostThreadMessage(threadId, WindowsNativeMethods.WmQuit, UIntPtr.Zero, IntPtr.Zero);
        if (thread is not null && !ReferenceEquals(thread, Thread.CurrentThread))
            thread.Join(TimeSpan.FromSeconds(2));
    }

    private static IntPtr RootWindowAt(WindowsNativeMethods.Point point)
    {
        var window = WindowsNativeMethods.WindowFromPoint(point);
        if (window == IntPtr.Zero) return IntPtr.Zero;
        var root = WindowsNativeMethods.GetAncestor(window, WindowsNativeMethods.GaRootOwner);
        return root != IntPtr.Zero
            ? root
            : WindowsNativeMethods.GetAncestor(window, WindowsNativeMethods.GaRoot);
    }

    internal static bool HaveBoundsChanged(
        WindowsNativeMethods.NativeRect initial,
        WindowsNativeMethods.NativeRect current) =>
        initial.Left != current.Left
        || initial.Top != current.Top
        || initial.Right != current.Right
        || initial.Bottom != current.Bottom;

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        StopCurrentRun();
        _inputEvents.Writer.TryComplete();
        try { _inputProcessor.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
    }

    private sealed class HookStartup
    {
        internal TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Exception? Exception { get; set; }
    }

    private readonly record struct MouseInput(
        WindowsNativeMethods.Point Point,
        bool IsPress,
        int Generation,
        DateTimeOffset OccurredAt);
}

public sealed class SelectionGestureEventArgs(SelectionGesture gesture) : EventArgs
{
    public SelectionGesture Gesture { get; } = gesture;
}
