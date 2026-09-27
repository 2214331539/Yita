using System.Runtime.InteropServices;
using System.Text;
using Yita.Core.Selection;

namespace Yita.Native.Windows;

public sealed class WindowsSelectionAdapter : ISelectionReader
{
    private readonly ISelectionReader _reader;

    public WindowsSelectionAdapter() => _reader = new SelectionReaderPipeline(new ISelectionReader[]
    {
        new WindowsUiAutomationSelectionReader(),
        new WindowsNativeControlSelectionReader(),
        new WindowsClipboardSelectionReader(),
    });

    public WindowsSelectionAdapter(
        Func<SelectionRequest, CancellationToken, Task<SelectionResult>> reader) =>
        _reader = new DelegateSelectionReader(reader);

    public Task<SelectionResult> ReadAsync(
        SelectionRequest request,
        CancellationToken cancellationToken = default) =>
        _reader.ReadAsync(request, cancellationToken);

    public void Dispose()
    {
        if (_reader is IDisposable disposable) disposable.Dispose();
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
        var originalSequence = WindowsNativeMethods.GetClipboardSequenceNumber();
        var originalText = TryReadText();
        if (originalSequence == 0)
            return SelectionResult.Failed(SelectionFailureKind.ClipboardUnavailable, "sequence-unavailable");

        string? selectedText = null;
        uint copiedSequence = originalSequence;
        IntPtr copiedOwner = IntPtr.Zero;
        var attempts = isWpsPdf ? 2 : 1;
        for (var attempt = 0; attempt < attempts && selectedText is null; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TrySendCopy(target.Value))
            {
                if (!isWpsPdf || attempt + 1 >= attempts)
                    return SelectionResult.Failed(SelectionFailureKind.UnsupportedApplication, "copy-not-sent");
            }
            else
            {
                var deadline = DateTime.UtcNow + (isWpsPdf ? WpsTimeout : Timeout);
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sequence = WindowsNativeMethods.GetClipboardSequenceNumber();
                    if (sequence != originalSequence)
                    {
                        var owner = WindowsNativeMethods.GetClipboardOwner();
                        var text = TryReadText();
                        if (owner != IntPtr.Zero
                            && IsClipboardOwnerFromTarget(owner, target.Value)
                            && !string.IsNullOrWhiteSpace(text))
                        {
                            selectedText = text.Trim();
                            copiedSequence = WindowsNativeMethods.GetClipboardSequenceNumber();
                            copiedOwner = owner;
                            break;
                        }
                    }
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                }
            }

            if (selectedText is null && attempt + 1 < attempts)
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        }

        if (selectedText is null)
            return SelectionResult.Failed(SelectionFailureKind.Empty, "copy-empty");

        // Restore only when the copied application still owns the clipboard.
        // If the original clipboard did not contain Unicode text, leave richer
        // formats untouched rather than clearing them blindly.
        if (WindowsNativeMethods.GetClipboardSequenceNumber() == copiedSequence
            && WindowsNativeMethods.GetClipboardOwner() == copiedOwner
            && originalText is not null)
            TryWriteText(originalText);

        return new SelectionResult(
            selectedText,
            SelectionSource.ClipboardFallback,
            new SelectionBounds(request.Pointer.X, request.Pointer.Y, 0, 0),
            SelectionFailureKind.None,
            isWpsPdf ? "wps-ctrl-c" : "ctrl-c");
    }

    private static bool TrySendCopy(SelectionTarget target)
    {
        if (!WindowsNativeMethods.IsForegroundTargetReady()) return false;
        var foreground = WindowsNativeMethods.GetForegroundWindow();
        var foregroundRoot = foreground == IntPtr.Zero
            ? IntPtr.Zero
            : WindowsNativeMethods.GetAncestor(foreground, WindowsNativeMethods.GaRootOwner);
        if (foregroundRoot == IntPtr.Zero) foregroundRoot = foreground;
        if (foregroundRoot != target.RootWindow) return false;

        var inputs = new[]
        {
            WindowsNativeMethods.KeyInput(WindowsNativeMethods.VirtualKeyControl),
            WindowsNativeMethods.KeyInput(WindowsNativeMethods.VirtualKeyC),
            WindowsNativeMethods.KeyInput(WindowsNativeMethods.VirtualKeyC, true),
            WindowsNativeMethods.KeyInput(WindowsNativeMethods.VirtualKeyControl, true),
        };
        return WindowsNativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<WindowsNativeMethods.Input>())
            == inputs.Length;
    }

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
            var pointer = WindowsNativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(pointer); }
            finally { WindowsNativeMethods.GlobalUnlock(handle); }
        }
        finally { WindowsNativeMethods.CloseClipboard(); }
    }

    private static bool TryWriteText(string value)
    {
        if (!WindowsNativeMethods.OpenClipboard(IntPtr.Zero)) return false;
        IntPtr memory = IntPtr.Zero;
        try
        {
            var bytes = Encoding.Unicode.GetBytes(value + "\0");
            memory = WindowsNativeMethods.GlobalAlloc(WindowsNativeMethods.GmemMoveable, (UIntPtr)bytes.Length);
            if (memory == IntPtr.Zero) return false;
            var pointer = WindowsNativeMethods.GlobalLock(memory);
            if (pointer == IntPtr.Zero) return false;
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { WindowsNativeMethods.GlobalUnlock(memory); }
            if (!WindowsNativeMethods.EmptyClipboard()) return false;
            if (WindowsNativeMethods.SetClipboardData(WindowsNativeMethods.CfUnicodeText, memory) == IntPtr.Zero)
                return false;
            memory = IntPtr.Zero;
            return true;
        }
        finally
        {
            if (memory != IntPtr.Zero) WindowsNativeMethods.GlobalFree(memory);
            WindowsNativeMethods.CloseClipboard();
        }
    }
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
            if (_thread is not null) return;
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows hotkey is unavailable.");
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
    private readonly WindowsGlobalHotkeyService _hotkey = new();
    private readonly WindowsMouseSelectionService _mouse = new();
    private readonly WindowsSelectionAdapter _reader = new();

    public event EventHandler<SelectionCapturedEventArgs>? SelectionCaptured;

    public bool IsRunning => _hotkey.IsRunning;

    public void Start()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows selection is unavailable.");
        _hotkey.TranslateRequested += OnTranslateRequested;
        _mouse.SelectionGestureCompleted += OnSelectionGestureCompleted;
        _hotkey.Start();
        _mouse.Start();
    }

    private async void OnTranslateRequested(object? sender, EventArgs e)
    {
        try
        {
            if (!WindowsNativeMethods.GetCursorPos(out var point)) return;
            if (!IsExternalPoint(new ScreenPoint(point.X, point.Y))) return;
            var request = new SelectionRequest(SelectionTrigger.TranslateShortcut, new ScreenPoint(point.X, point.Y));
            var result = await _reader.ReadAsync(request).ConfigureAwait(false);
            SelectionCaptured?.Invoke(this, new SelectionCapturedEventArgs(request, result));
        }
        catch { }
    }

    private async void OnSelectionGestureCompleted(object? sender, SelectionGestureEventArgs args)
    {
        try
        {
            var gesture = args.Gesture;
            if (!IsExternalGesture(gesture)) return;

            // Give the target application one short frame to commit its
            // selection before UIA or Ctrl+C reads it.
            await Task.Delay(80).ConfigureAwait(false);
            var request = new SelectionRequest(
                SelectionTrigger.MouseGesture,
                gesture.End,
                GetProcessNameAt(gesture.End),
                gesture.Bounds);
            var result = await _reader.ReadAsync(request).ConfigureAwait(false);
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
        _hotkey.TranslateRequested -= OnTranslateRequested;
        _mouse.SelectionGestureCompleted -= OnSelectionGestureCompleted;
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
