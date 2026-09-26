using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Yita.Interop;
using Yita.Models;

namespace Yita.Hooks;

/// <summary>
/// Owns the low-level mouse hook used to detect completed text-selection
/// gestures. The hook can be rebound without recreating subscribers, which is
/// useful after Windows resumes or finishes starting the desktop shell.
/// </summary>
internal sealed class GlobalMouseHook : IDisposable
{
    private readonly object _operationSync = new();
    private readonly object _lifecycleSync = new();
    private readonly NativeMethods.LowLevelMouseProc _hookCallback;
    private readonly SelectionGestureDetector _gestureDetector;
    private readonly NativeWindowDragTracker _windowDragTracker = new();
    private readonly Channel<MouseInputEvent> _inputEvents;
    private readonly Task _inputProcessor;
    private Thread? _hookThread;
    private IntPtr _hookHandle;
    private uint _hookThreadId;
    private bool _isStopping;
    private bool _disposed;
    private int _isRunning;
    private int _runGeneration;
    private long _lastMouseInputUtcTicks;

    public GlobalMouseHook()
    {
        _hookCallback = HookCallback;
        _gestureDetector = new SelectionGestureDetector(
            NativeMethods.GetSystemMetrics(NativeMethods.SmCxDrag),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCyDrag));
        _inputEvents = Channel.CreateUnbounded<MouseInputEvent>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        _inputProcessor = Task.Run(ProcessInputEventsAsync);
    }

    public event Action<ScreenPoint>? MousePressed;

    public event Action<SelectionGesture>? SelectionGestureCompleted;

    /// <summary>
    /// Raised if the native message loop exits without an explicit stop. Windows
    /// can also remove a timed-out low-level hook without ending this loop, so
    /// the app additionally performs a conservative periodic rebind.
    /// </summary>
    public event Action? HookStoppedUnexpectedly;

    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    public DateTimeOffset? LastMouseInputAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastMouseInputUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void Start()
    {
        lock (_operationSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StartCore();
        }
    }

    /// <summary>
    /// Stops the current hook message loop and creates a new one while keeping
    /// the same event subscriptions. This is intentionally safe to call even
    /// when the hook was already stopped.
    /// </summary>
    public void Restart()
    {
        lock (_operationSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCurrentRun();
            ObjectDisposedException.ThrowIf(_disposed, this);
            StartCore();
        }
    }

    public string CreateStatusReport(bool useChinese)
    {
        var lastInput = LastMouseInputAtUtc;
        if (useChinese)
        {
            return string.Join(
                Environment.NewLine,
                "输入捕获状态",
                $"全局鼠标钩子：{(IsRunning ? "运行中" : "未运行")}",
                $"最近一次鼠标事件：{(lastInput is null ? "尚未收到" : lastInput.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))}",
                "此报告不包含选中文本、译文或凭据。");
        }

        return string.Join(
            Environment.NewLine,
            "Input capture status",
            $"Global mouse hook: {(IsRunning ? "running" : "not running")}",
            $"Last mouse event: {(lastInput is null ? "not received yet" : lastInput.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))}",
            "This report contains no selected text, translations, or credentials.");
    }

    private void StartCore()
    {
        HookStartup? startup = null;
        Thread? hookThread = null;
        lock (_lifecycleSync)
        {
            if (_hookThread is { IsAlive: true })
            {
                return;
            }

            _isStopping = false;
            _hookHandle = IntPtr.Zero;
            _hookThreadId = 0;
            Volatile.Write(ref _isRunning, 0);
            var generation = unchecked(Interlocked.Increment(ref _runGeneration));
            startup = new HookStartup();
            hookThread = new Thread(() => HookThreadMain(startup, generation))
            {
                IsBackground = true,
                Name = "Yita.MouseHook",
            };
            hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread = hookThread;
        }

        hookThread.Start();

        if (!startup.Completion.Task.Wait(TimeSpan.FromSeconds(3)))
        {
            StopCurrentRun();
            throw new TimeoutException("全局鼠标钩子启动超时。");
        }

        if (startup.Completion.Task.Result is { } startupException)
        {
            StopCurrentRun();
            throw new InvalidOperationException("无法安装全局鼠标钩子。", startupException);
        }
    }

    private void HookThreadMain(HookStartup startup, int generation)
    {
        IntPtr hookHandle = IntPtr.Zero;
        var installed = false;
        try
        {
            var hookThreadId = NativeMethods.GetCurrentThreadId();
            hookHandle = NativeMethods.SetWindowsHookEx(
                NativeMethods.WhMouseLl,
                _hookCallback,
                NativeMethods.GetModuleHandle(null),
                0);
            if (hookHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            lock (_lifecycleSync)
            {
                if (!ReferenceEquals(_hookThread, Thread.CurrentThread))
                {
                    throw new InvalidOperationException("全局鼠标钩子生命周期发生冲突。");
                }

                _hookHandle = hookHandle;
                _hookThreadId = hookThreadId;
            }

            installed = true;
            Volatile.Write(ref _isRunning, 1);
        }
        catch (Exception exception)
        {
            startup.Exception = exception;
        }
        finally
        {
            startup.Completion.TrySetResult(startup.Exception);
        }

        if (!installed)
        {
            CleanupRun(hookHandle, notifyUnexpectedStop: false);
            return;
        }

        var unexpectedStop = false;
        try
        {
            var messageResult = 0;
            while ((messageResult = NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0)) > 0)
            {
            }

            unexpectedStop = messageResult < 0;
        }
        catch (Exception exception)
        {
            unexpectedStop = true;
            System.Diagnostics.Debug.WriteLine($"Yita mouse hook loop failed: {exception}");
        }
        finally
        {
            CleanupRun(hookHandle, notifyUnexpectedStop: unexpectedStop || IsUnexpectedStopRequested());
        }
    }

    private bool IsUnexpectedStopRequested()
    {
        lock (_lifecycleSync)
        {
            return !_disposed && !_isStopping;
        }
    }

    private void CleanupRun(IntPtr hookHandle, bool notifyUnexpectedStop)
    {
        if (hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(hookHandle);
        }

        var shouldNotify = false;
        lock (_lifecycleSync)
        {
            if (ReferenceEquals(_hookThread, Thread.CurrentThread))
            {
                _hookThread = null;
                _hookHandle = IntPtr.Zero;
                _hookThreadId = 0;
                shouldNotify = notifyUnexpectedStop && !_disposed && !_isStopping;
            }
        }

        Volatile.Write(ref _isRunning, 0);
        if (shouldNotify)
        {
            InvokeSafely(HookStoppedUnexpectedly);
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = unchecked((int)(long)wParam);
            if (message is NativeMethods.WmLButtonDown or NativeMethods.WmLButtonUp)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MouseHookData>(lParam);
                if ((data.Flags & NativeMethods.LlMhfInjected) == 0)
                {
                    Interlocked.Exchange(ref _lastMouseInputUtcTicks, DateTimeOffset.UtcNow.Ticks);
                    // A low-level hook can be silently removed by Windows if its
                    // callback blocks. Copy the tiny native payload and return;
                    // hit testing and subscribers run on the ordered worker below.
                    _inputEvents.Writer.TryWrite(new MouseInputEvent(
                        data.Point.ToScreenPoint(),
                        message == NativeMethods.WmLButtonDown,
                        Volatile.Read(ref _runGeneration),
                        DateTimeOffset.UtcNow));
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private async Task ProcessInputEventsAsync()
    {
        var activeGeneration = 0;
        await foreach (var input in _inputEvents.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (_disposed)
                {
                    continue;
                }

                if (input.Generation != activeGeneration)
                {
                    _windowDragTracker.Cancel();
                    _gestureDetector.Cancel();
                    activeGeneration = input.Generation;
                }

                if (input.IsPress)
                {
                    _windowDragTracker.Press(input.Point);
                    _gestureDetector.Press(input.Point);
                    InvokeSafely(MousePressed, input.Point);
                    continue;
                }

                var suppressSelection = _windowDragTracker.ReleaseShouldSuppressSelection();
                var gesture = _gestureDetector.Release(input.Point, input.OccurredAt);
                if (!suppressSelection && gesture is not null)
                {
                    InvokeSafely(SelectionGestureCompleted, gesture.Value);
                }
            }
            catch (Exception exception)
            {
                _windowDragTracker.Cancel();
                _gestureDetector.Cancel();
                System.Diagnostics.Debug.WriteLine(
                    $"Yita mouse input worker failed: {exception}");
            }
        }
    }

    private static void InvokeSafely<T>(Action<T>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(value);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Yita mouse hook subscriber failed: {exception}");
            }
        }
    }

    private static void InvokeSafely(Action? handlers)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Yita mouse hook lifecycle subscriber failed: {exception}");
            }
        }
    }

    private void StopCurrentRun()
    {
        Thread? hookThread;
        uint hookThreadId;
        lock (_lifecycleSync)
        {
            _isStopping = true;
            hookThread = _hookThread;
            hookThreadId = _hookThreadId;
        }

        if (hookThreadId != 0)
        {
            NativeMethods.PostThreadMessage(hookThreadId, NativeMethods.WmQuit, UIntPtr.Zero, IntPtr.Zero);
        }

        if (hookThread is not null
            && !ReferenceEquals(hookThread, Thread.CurrentThread)
            && !hookThread.Join(TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("全局鼠标钩子停止超时。");
        }
    }

    public void Dispose()
    {
        lock (_operationSync)
        {
            lock (_lifecycleSync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _isStopping = true;
            }

            try
            {
                StopCurrentRun();
            }
            catch (TimeoutException exception)
            {
                System.Diagnostics.Debug.WriteLine($"Yita mouse hook did not stop promptly: {exception}");
            }

            _inputEvents.Writer.TryComplete();
        }

        try
        {
            _inputProcessor.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Yita mouse input worker did not stop cleanly: {exception}");
        }
    }

    private sealed class HookStartup
    {
        internal TaskCompletionSource<Exception?> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Exception? Exception { get; set; }
    }

    private readonly record struct MouseInputEvent(
        ScreenPoint Point,
        bool IsPress,
        int Generation,
        DateTimeOffset OccurredAt);
}
