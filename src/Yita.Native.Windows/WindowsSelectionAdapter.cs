using System.Runtime.InteropServices;
using System.Text;
using Yita.Core;
using Yita.Core.Selection;
using Yita.Services;

namespace Yita.Native.Windows;

public sealed class WindowsSelectionAdapter : ISelectionReader
{
    private readonly ISelectionReader _fullReader;
    private readonly ISelectionReader _accessibleReader;
    private readonly WindowsUiAutomationSelectionReader? _uiaReader = null;
    private readonly WindowsClipboardSelectionReader _clipboardReader = new();
    private readonly Func<SelectionRequest, CancellationToken, Task<SelectionResult>>? _override = null;

    public bool UseClipboardFallback { get; set; }
    public bool UseWpsPdfCompatibility { get; set; } = true;
    public bool UseSelectionContext { get; set; }

    public WindowsSelectionAdapter()
    {
        _uiaReader = new WindowsUiAutomationSelectionReader();
        var nativeReader = new WindowsNativeControlSelectionReader();
        _accessibleReader = new SelectionReaderPipeline(new ISelectionReader[]
        {
            _uiaReader,
            nativeReader,
        });
        _fullReader = new SelectionReaderPipeline(new ISelectionReader[]
        {
            _uiaReader,
            nativeReader,
            new WindowsClipboardSelectionReader(),
        });
    }

    public WindowsSelectionAdapter(
        Func<SelectionRequest, CancellationToken, Task<SelectionResult>> reader) =>
        (_fullReader, _accessibleReader, _override) =
        (new DelegateSelectionReader(reader), new DelegateSelectionReader(reader), reader);

    public async Task<SelectionResult> ReadAsync(
        SelectionRequest request,
        CancellationToken cancellationToken = default)
    {
        request = request with { IncludeContext = UseSelectionContext };
        if (_override is not null) return await _fullReader.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Trigger != SelectionTrigger.MouseGesture)
            return await WindowsClipboardSelectionReader.ReadExistingAsync(request, cancellationToken).ConfigureAwait(false);
        if (UseWpsPdfCompatibility && WindowsClipboardSelectionReader.IsWpsPdfRequest(request))
        {
            var copied = await _clipboardReader.ReadAsync(request, cancellationToken).ConfigureAwait(false);
            if (copied.Succeeded || copied.Failure is SelectionFailureKind.ProtectedContent or SelectionFailureKind.Cancelled) return copied;
            return await _accessibleReader.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        }
        return await (UseClipboardFallback ? _fullReader : _accessibleReader).ReadAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _uiaReader?.Dispose();
    }
}

internal sealed class DelegateSelectionReader(
    Func<SelectionRequest, CancellationToken, Task<SelectionResult>> reader) : ISelectionReader
{
    public Task<SelectionResult> ReadAsync(SelectionRequest request, CancellationToken cancellationToken = default) =>
        reader(request, cancellationToken);
}

/// <summary>
/// Keyboard-only fallback for applications that do not expose selected text
/// through UI Automation. The transaction is serialized and bounded so a
/// clipboard failure cannot block the desktop shell.
/// </summary>
public sealed class WindowsClipboardSelectionReader : ISelectionReader
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan WpsTimeout = TimeSpan.FromMilliseconds(1_000);
    private readonly Func<SelectionRequest, CancellationToken, Task<SelectionResult>>? _override;

    public WindowsClipboardSelectionReader() { }

    internal static bool IsWpsPdfRequest(SelectionRequest request)
    {
        if (!OperatingSystem.IsWindows() || request.ForegroundApplication is not ("wpspdf" or "wps")) return false;
        var target = ResolveTarget(request.Pointer);
        return target is not null && IsWpsPdfProcess(target.Value.ProcessId, target.Value.RootWindow);
    }

    internal static async Task<SelectionResult> ReadExistingAsync(SelectionRequest request, CancellationToken token)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var text = TryReadText();
            if (!string.IsNullOrWhiteSpace(text)) return new SelectionResult(text, SelectionSource.ManualClipboard);
            await Task.Delay(20, token).ConfigureAwait(false);
        }
        return SelectionResult.Failed(SelectionFailureKind.ClipboardUnavailable, "manual-clipboard-empty");
    }

    internal WindowsClipboardSelectionReader(
        Func<SelectionRequest, CancellationToken, Task<SelectionResult>> overrideReader) =>
        _override = overrideReader;

    public async Task<SelectionResult> ReadAsync(
        SelectionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_override is not null) return await _override(request, cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
            return SelectionResult.Failed(SelectionFailureKind.UnsupportedApplication, "windows-only");

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadClipboardTransactionAsync(request, cancellationToken).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    private static async Task<SelectionResult> ReadClipboardTransactionAsync(
        SelectionRequest request,
        CancellationToken cancellationToken)
    {
        var target = ResolveTarget(request.Pointer);
        if (target is null)
            return SelectionResult.Failed(SelectionFailureKind.UnsupportedApplication, "target-unavailable");

        var isWpsPdf = IsWpsPdfProcess(target.Value.ProcessId, target.Value.RootWindow);
        using var snapshot = WindowsClipboardSnapshot.TryCapture();
        if (snapshot is null || snapshot.Sequence == 0)
            return SelectionResult.Failed(SelectionFailureKind.ClipboardUnavailable, "clipboard-cannot-preserve");
        var originalSequence = snapshot.Sequence;

        string? selectedText = null;
        uint copiedSequence = originalSequence;
        IntPtr copiedOwner = IntPtr.Zero;
        var attempts = isWpsPdf ? 2 : 1;
        try
        {
        for (var attempt = 0; attempt < attempts && selectedText is null; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WindowsNativeMethods.GetClipboardSequenceNumber() != copiedSequence)
                return SelectionResult.Failed(SelectionFailureKind.Cancelled, "clipboard-changed-by-user");
            if (!TrySendCopy(target.Value, out var sendFailure))
            {
                if (!isWpsPdf || attempt + 1 >= attempts)
                    return SelectionResult.Failed(SelectionFailureKind.UnsupportedApplication, sendFailure);
            }
            else
            {
                // Once copy has been delivered, finish observing and restoring the
                // clipboard before honoring cancellation from a newer selection.
                var deadline = DateTime.UtcNow + (isWpsPdf ? WpsTimeout : Timeout);
                var attemptSequence = copiedSequence;
                while (DateTime.UtcNow < deadline)
                {
                    var sequence = WindowsNativeMethods.GetClipboardSequenceNumber();
                    if (sequence != attemptSequence)
                    {
                        var owner = WindowsNativeMethods.GetClipboardOwner();
                        if (owner != IntPtr.Zero && IsClipboardOwnerFromTarget(owner, target.Value))
                        {
                            copiedSequence = sequence;
                            copiedOwner = owner;
                        }
                        var text = TryReadText();
                        if (sequence != WindowsNativeMethods.GetClipboardSequenceNumber())
                            return SelectionResult.Failed(SelectionFailureKind.Cancelled, "clipboard-changed-during-read");
                        if (owner != IntPtr.Zero
                            && IsClipboardOwnerFromTarget(owner, target.Value))
                        {
                            copiedSequence = sequence;
                            copiedOwner = owner;
                            if (!string.IsNullOrWhiteSpace(text)) { selectedText = text.Trim(); break; }
                        }
                        else return SelectionResult.Failed(SelectionFailureKind.Cancelled, "clipboard-owner-changed");
                    }
                    await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (selectedText is null && attempt + 1 < attempts)
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        }

        if (selectedText is null)
            return SelectionResult.Failed(SelectionFailureKind.Empty, "copy-empty");

        return new SelectionResult(
            selectedText,
            SelectionSource.ClipboardFallback,
            new SelectionBounds(request.Pointer.X, request.Pointer.Y, 0, 0),
            SelectionFailureKind.None,
            isWpsPdf ? "wps-ctrl-c" : "ctrl-c");
        }
        finally
        {
            if (copiedOwner != IntPtr.Zero)
            {
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    if (WindowsNativeMethods.GetClipboardSequenceNumber() != copiedSequence
                        || WindowsNativeMethods.GetClipboardOwner() != copiedOwner || snapshot.TryRestore(copiedSequence, copiedOwner)) break;
                    await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }

    private static bool TrySendCopy(SelectionTarget target, out string failure)
    {
        failure = "copy-target-not-foreground";
        if (!WindowsNativeMethods.IsForegroundTargetReady()) return false;
        var foreground = WindowsNativeMethods.GetForegroundWindow();
        var foregroundRoot = foreground == IntPtr.Zero
            ? IntPtr.Zero
            : WindowsNativeMethods.GetAncestor(foreground, WindowsNativeMethods.GaRootOwner);
        if (foregroundRoot == IntPtr.Zero) foregroundRoot = foreground;
        if (foregroundRoot != target.RootWindow) return false;
        failure = "copy-user-keys-held";
        foreach (var key in new[] { 0x01, 0x02, 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x43 })
            if ((GetAsyncKeyState(key) & 0x8000) != 0) return false;
        failure = "copy-focus-unavailable";
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(0, ref info) || info.Focus == IntPtr.Zero) return false;
        var className = new StringBuilder(256);
        WindowsNativeMethods.GetClassName(info.Focus, className, className.Capacity);
        failure = "copy-password-control";
        if (className.ToString().Contains("edit", StringComparison.OrdinalIgnoreCase)
            && (WindowsNativeMethods.GetWindowLongPtr(info.Focus, -16).ToInt64() & 0x20) != 0) return false;

        var inputs = new[]
        {
            WindowsNativeMethods.KeyInput(WindowsNativeMethods.VirtualKeyControl),
            WindowsNativeMethods.KeyInput(WindowsNativeMethods.VirtualKeyC),
            WindowsNativeMethods.KeyInput(WindowsNativeMethods.VirtualKeyC, true),
            WindowsNativeMethods.KeyInput(WindowsNativeMethods.VirtualKeyControl, true),
        };
        failure = "copy-input-rejected";
        var sent = WindowsNativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<WindowsNativeMethods.Input>());
        if (sent > 0 && sent < inputs.Length)
        {
            var releases = sent >= 2 ? new[] { inputs[2], inputs[3] } : new[] { inputs[3] };
            WindowsNativeMethods.SendInput((uint)releases.Length, releases, Marshal.SizeOf<WindowsNativeMethods.Input>());
        }
        // C-down can already initiate copying even if SendInput was partial.
        return sent >= 2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public WindowsNativeMethods.NativeRect CaretRect;
    }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    private static SelectionTarget? ResolveTarget(ScreenPoint point)
    {
        var hit = WindowsNativeMethods.WindowFromPoint(new WindowsNativeMethods.Point
        {
            X = (int)Math.Round(point.X),
            Y = (int)Math.Round(point.Y),
        });
        if (hit == IntPtr.Zero) return null;

        var root = WindowsNativeMethods.GetAncestor(hit, WindowsNativeMethods.GaRootOwner);
        if (root == IntPtr.Zero) root = WindowsNativeMethods.GetAncestor(hit, WindowsNativeMethods.GaRoot);
        if (root == IntPtr.Zero) return null;

        WindowsNativeMethods.GetWindowThreadProcessId(root, out var processId);
        return processId == 0 || processId == (uint)Environment.ProcessId
            ? null
            : new SelectionTarget(root, processId);
    }

    private static bool IsWpsPdfProcess(uint processId, IntPtr rootWindow)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(checked((int)processId));
            if (process.ProcessName.Equals("wpspdf", StringComparison.OrdinalIgnoreCase)) return true;
            if (!process.ProcessName.Equals("wps", StringComparison.OrdinalIgnoreCase)) return false;

            var title = new StringBuilder(512);
            return WindowsNativeMethods.GetWindowText(rootWindow, title, title.Capacity) > 0
                && title.ToString().Contains("pdf", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or OverflowException)
        {
            return false;
        }
    }

    private static bool IsClipboardOwnerFromTarget(IntPtr owner, SelectionTarget target)
    {
        WindowsNativeMethods.GetWindowThreadProcessId(owner, out var ownerProcessId);
        if (ownerProcessId == target.ProcessId) return true;

        var ownerRoot = WindowsNativeMethods.GetAncestor(owner, WindowsNativeMethods.GaRootOwner);
        return ownerRoot != IntPtr.Zero && ownerRoot == target.RootWindow;
    }

    private readonly record struct SelectionTarget(IntPtr RootWindow, uint ProcessId);

    private static string? TryReadText()
    {
        if (!WindowsNativeMethods.OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var handle = WindowsNativeMethods.GetClipboardData(WindowsNativeMethods.CfUnicodeText);
            if (handle == IntPtr.Zero) return null;
            var size = GlobalSize(handle).ToUInt64();
            if (size < 2 || size > 1024 * 1024 || size % 2 != 0) return null;
            var pointer = WindowsNativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero) return null;
            try
            {
                var value = Marshal.PtrToStringUni(pointer, (int)size / 2);
                var terminator = value?.IndexOf('\0') ?? -1;
                return terminator >= 0 ? value![..terminator] : null;
            }
            finally { WindowsNativeMethods.GlobalUnlock(handle); }
        }
        finally { WindowsNativeMethods.CloseClipboard(); }
    }

    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
}

public interface IWindowsHotkeyService : IDisposable
{
    event EventHandler? TranslateRequested;

    bool IsRunning { get; }

    void Start();
}

/// <summary>Registers Ctrl+Shift+T on a dedicated message-loop thread.</summary>
public sealed class WindowsGlobalHotkeyService : IWindowsHotkeyService
{
    private const int HotkeyId = 0x5949;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _started = new(false);
    private Thread? _thread;
    private uint _threadId;
    private Exception? _startupError;
    private bool _disposed;

    public event EventHandler? TranslateRequested;

    public bool IsRunning => Volatile.Read(ref _threadId) != 0;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is { IsAlive: true }) return;
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows hotkey is unavailable.");
            _started.Reset();
            _startupError = null;
            _thread = new Thread(MessageLoop) { IsBackground = true, Name = "Yita.CrossPlatform.Hotkey" };
            _thread.Start();
        }
        if (!_started.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("全局快捷键启动超时。");
        if (_startupError is not null) throw new InvalidOperationException("无法注册 Ctrl+Shift+T。", _startupError);
    }

    private void MessageLoop()
    {
        var registered = false;
        try
        {
            _ = WindowsNativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, WindowsNativeMethods.PmNoRemove);
            Volatile.Write(ref _threadId, WindowsNativeMethods.GetCurrentThreadId());
            registered = WindowsNativeMethods.RegisterHotKey(
                IntPtr.Zero, HotkeyId,
                WindowsNativeMethods.ModControl | WindowsNativeMethods.ModShift | WindowsNativeMethods.ModNoRepeat,
                WindowsNativeMethods.VirtualKeyT);
            if (!registered) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception exception)
        {
            _startupError = exception;
            _started.Set();
            Volatile.Write(ref _threadId, 0);
            return;
        }

        _started.Set();
        try
        {
            while (WindowsNativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Value == WindowsNativeMethods.WmHotKey
                    && unchecked((int)message.WParam.ToUInt64()) == HotkeyId)
                {
                    try { TranslateRequested?.Invoke(this, EventArgs.Empty); } catch { }
                }
            }
        }
        finally
        {
            if (registered) WindowsNativeMethods.UnregisterHotKey(IntPtr.Zero, HotkeyId);
            Volatile.Write(ref _threadId, 0);
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            thread = _thread;
        }
        var id = Volatile.Read(ref _threadId);
        if (id != 0) WindowsNativeMethods.PostThreadMessage(id, WindowsNativeMethods.WmQuit, UIntPtr.Zero, IntPtr.Zero);
        thread?.Join(TimeSpan.FromSeconds(2));
        _started.Dispose();
    }
}

public sealed class WindowsSelectionRuntime : IDisposable
{
    public event EventHandler<ScreenPoint>? ExternalPointerPressed;
    private readonly WindowsGlobalHotkeyService _hotkey = new();
    private readonly WindowsMouseSelectionService _mouse = new();
    private readonly WindowsSelectionAdapter _reader = new();
    private readonly LatestRequestController _requests = new();
    private volatile bool _isEnabled;
    private volatile int _selectionDelayMilliseconds = 80;
    private readonly RuntimeHealthJournal _health = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yita", "desktop-runtime-health.log"));
    private Timer? _recoveryTimer;
    private int _repairing;
    private bool _started;
    private bool _disposed;

    public event EventHandler<SelectionCapturedEventArgs>? SelectionCaptured;

    public bool IsRunning => _mouse.IsRunning;
    public bool IsHotkeyRunning => _hotkey.IsRunning;

    public void Configure(bool isEnabled, bool useClipboardFallback, int selectionDelayMilliseconds,
        bool useWpsPdfCompatibility = true, bool useSelectionContext = false)
    {
        _isEnabled = isEnabled;
        _selectionDelayMilliseconds = Math.Clamp(selectionDelayMilliseconds, 0, 2_000);
        _reader.UseClipboardFallback = useClipboardFallback;
        _reader.UseWpsPdfCompatibility = useWpsPdfCompatibility;
        _reader.UseSelectionContext = useSelectionContext;
        if (!isEnabled) _requests.Cancel();
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows selection is unavailable.");
        if (_started) return;
        _started = true;
        _hotkey.TranslateRequested += OnTranslateRequested;
        _mouse.SelectionGestureCompleted += OnSelectionGestureCompleted;
        _mouse.ExternalPointerPressed += OnExternalPointerPressed;
        try { _hotkey.Start(); _health.Record(RuntimeHealthEvent.HotkeyStarted); }
        catch (Exception exception) { _health.Record(RuntimeHealthEvent.HotkeyRegistrationFailed, exception); }
        try { _mouse.Start(); _health.Record(RuntimeHealthEvent.MouseHookStarted); }
        catch (Exception exception) { _health.Record(RuntimeHealthEvent.MouseHookStoppedUnexpectedly, exception); }
        _recoveryTimer = new Timer(_ => RepairInputCapture(), null, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
    }

    public void RepairInputCapture()
    {
        if (_disposed || Interlocked.CompareExchange(ref _repairing, 1, 0) != 0) return;
        try
        {
            try { _mouse.Rebind(); _health.Record(RuntimeHealthEvent.MouseHookRecoverySucceeded); }
            catch (Exception exception) { _health.Record(RuntimeHealthEvent.MouseHookRecoveryFailed, exception); }
            if (!_hotkey.IsRunning)
            {
                try { _hotkey.Start(); _health.Record(RuntimeHealthEvent.HotkeyRecovered); }
                catch (Exception exception) { _health.Record(RuntimeHealthEvent.HotkeyRegistrationFailed, exception); }
            }
        }
        finally { Volatile.Write(ref _repairing, 0); }
    }

    public string CreateDiagnostics(bool chinese) => _health.CreateReport(chinese)
        + Environment.NewLine + $"MouseHook={IsRunning}; Hotkey={IsHotkeyRunning}";

    public void TranslateClipboard() => OnTranslateRequested(this, EventArgs.Empty);

    private void OnExternalPointerPressed(object? sender, ScreenPoint point)
    {
        if (_disposed) return;
        _requests.Cancel();
        try { ExternalPointerPressed?.Invoke(this, point); } catch { }
    }

    private async void OnTranslateRequested(object? sender, EventArgs e)
    {
        try
        {
            if (!WindowsNativeMethods.GetCursorPos(out var point)) return;
            using var pending = _requests.Begin();
            var request = new SelectionRequest(SelectionTrigger.TranslateShortcut, new ScreenPoint(point.X, point.Y));
            var result = await _reader.ReadAsync(request, pending.Token).ConfigureAwait(false);
            if (pending.IsCurrent)
                SelectionCaptured?.Invoke(this, new SelectionCapturedEventArgs(request, result));
        }
        catch { }
    }

    private async void OnSelectionGestureCompleted(object? sender, SelectionGestureEventArgs args)
    {
        try
        {
            var gesture = args.Gesture;
            if (!_isEnabled) return;
            if (!IsExternalGesture(gesture)) return;
            using var pending = _requests.Begin();

            // Give the target application one short frame to commit its
            // selection before UIA or Ctrl+C reads it.
            if (_selectionDelayMilliseconds > 0)
                await Task.Delay(_selectionDelayMilliseconds, pending.Token).ConfigureAwait(false);
            if (!_isEnabled || !pending.IsCurrent || !IsExternalGesture(gesture)) return;
            var request = new SelectionRequest(
                SelectionTrigger.MouseGesture,
                gesture.End,
                GetProcessNameAt(gesture.End),
                gesture.Bounds);
            var result = await _reader.ReadAsync(request, pending.Token).ConfigureAwait(false);
            if (_isEnabled && pending.IsCurrent)
                SelectionCaptured?.Invoke(this, new SelectionCapturedEventArgs(request, result));
        }
        catch
        {
            // A hook subscriber or native reader must never terminate the
            // low-level mouse loop.
        }
    }

    private static bool IsExternalGesture(SelectionGesture gesture)
    {
        var startRoot = RootWindowAt(gesture.Start);
        var endRoot = RootWindowAt(gesture.End);
        if (startRoot == IntPtr.Zero || startRoot != endRoot) return false;

        WindowsNativeMethods.GetWindowThreadProcessId(endRoot, out var processId);
        return processId != 0 && processId != (uint)Environment.ProcessId;
    }

    private static bool IsExternalPoint(ScreenPoint point)
    {
        var root = RootWindowAt(point);
        if (root == IntPtr.Zero) return false;
        WindowsNativeMethods.GetWindowThreadProcessId(root, out var processId);
        return processId != 0 && processId != (uint)Environment.ProcessId;
    }

    private static IntPtr RootWindowAt(ScreenPoint point)
    {
        var window = WindowsNativeMethods.WindowFromPoint(new WindowsNativeMethods.Point
        {
            X = (int)Math.Round(point.X),
            Y = (int)Math.Round(point.Y),
        });
        if (window == IntPtr.Zero) return IntPtr.Zero;
        var root = WindowsNativeMethods.GetAncestor(window, WindowsNativeMethods.GaRootOwner);
        return root != IntPtr.Zero
            ? root
            : WindowsNativeMethods.GetAncestor(window, WindowsNativeMethods.GaRoot);
    }

    private static string? GetProcessNameAt(ScreenPoint point)
    {
        var root = RootWindowAt(point);
        if (root == IntPtr.Zero) return null;
        WindowsNativeMethods.GetWindowThreadProcessId(root, out var processId);
        if (processId == 0) return null;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(checked((int)processId));
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or OverflowException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recoveryTimer?.Dispose();
        _isEnabled = false;
        _requests.Dispose();
        _hotkey.TranslateRequested -= OnTranslateRequested;
        _mouse.SelectionGestureCompleted -= OnSelectionGestureCompleted;
        _mouse.ExternalPointerPressed -= OnExternalPointerPressed;
        _hotkey.Dispose();
        _mouse.Dispose();
        _reader.Dispose();
    }
}

public sealed class SelectionCapturedEventArgs(SelectionRequest request, SelectionResult result) : EventArgs
{
    public SelectionRequest Request { get; } = request;
    public SelectionResult Result { get; } = result;
}
