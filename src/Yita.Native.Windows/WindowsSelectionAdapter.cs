using System.Runtime.InteropServices;
using System.Text;
using Yita.Core.Selection;

namespace Yita.Native.Windows;

public sealed class WindowsSelectionAdapter : ISelectionReader
{
    private readonly WindowsClipboardSelectionReader _reader;

    public WindowsSelectionAdapter() => _reader = new WindowsClipboardSelectionReader();

    public WindowsSelectionAdapter(
        Func<SelectionRequest, CancellationToken, Task<SelectionResult>> reader) =>
        _reader = new WindowsClipboardSelectionReader(reader);

    public Task<SelectionResult> ReadAsync(
        SelectionRequest request,
        CancellationToken cancellationToken = default) =>
        _reader.ReadAsync(request, cancellationToken);
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
        var originalSequence = WindowsNativeMethods.GetClipboardSequenceNumber();
        var originalText = TryReadText();
        if (originalSequence == 0)
            return SelectionResult.Failed(SelectionFailureKind.ClipboardUnavailable, "sequence-unavailable");
        if (!TrySendCopy())
            return SelectionResult.Failed(SelectionFailureKind.UnsupportedApplication, "copy-not-sent");

        var deadline = DateTime.UtcNow + Timeout;
        string? selectedText = null;
        uint copiedSequence = originalSequence;
        IntPtr copiedOwner = IntPtr.Zero;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sequence = WindowsNativeMethods.GetClipboardSequenceNumber();
            if (sequence != originalSequence)
            {
                var owner = WindowsNativeMethods.GetClipboardOwner();
                var text = TryReadText();
                if (owner != IntPtr.Zero && !string.IsNullOrWhiteSpace(text))
                {
                    selectedText = text.Trim();
                    copiedSequence = WindowsNativeMethods.GetClipboardSequenceNumber();
                    copiedOwner = owner;
                    break;
                }
            }
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
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
            "ctrl-c");
    }

    private static bool TrySendCopy()
    {
        if (!WindowsNativeMethods.IsForegroundTargetReady()) return false;
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
    private readonly WindowsSelectionAdapter _reader = new();

    public event EventHandler<SelectionCapturedEventArgs>? SelectionCaptured;

    public bool IsRunning => _hotkey.IsRunning;

    public void Start()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows selection is unavailable.");
        _hotkey.TranslateRequested += OnTranslateRequested;
        _hotkey.Start();
    }

    private async void OnTranslateRequested(object? sender, EventArgs e)
    {
        try
        {
            if (!WindowsNativeMethods.GetCursorPos(out var point)) return;
            var request = new SelectionRequest(SelectionTrigger.TranslateShortcut, new ScreenPoint(point.X, point.Y));
            var result = await _reader.ReadAsync(request).ConfigureAwait(false);
            SelectionCaptured?.Invoke(this, new SelectionCapturedEventArgs(request, result));
        }
        catch { }
    }

    public void Dispose()
    {
        _hotkey.TranslateRequested -= OnTranslateRequested;
        _hotkey.Dispose();
    }
}

public sealed class SelectionCapturedEventArgs(SelectionRequest request, SelectionResult result) : EventArgs
{
    public SelectionRequest Request { get; } = request;
    public SelectionResult Result { get; } = result;
}
