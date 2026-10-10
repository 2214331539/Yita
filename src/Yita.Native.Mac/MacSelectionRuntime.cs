using Yita.Core;
using Yita.Core.Platform;
using Yita.Core.Selection;

namespace Yita.Native.Mac;

/// <summary>Consumes bounded native input batches; selection and clipboard work stays outside callbacks.</summary>
public sealed class MacSelectionRuntime : ISelectionRuntime, IPlatformPermissionService, IDesktopSessionRuntime
{
    private readonly IMacHelperClient _helper;
    private readonly MacSelectionAdapter _reader;
    private readonly Func<ScreenPoint, CancellationToken, Task<bool>> _isOwnWindow;
    private readonly Func<bool> _isMac;
    private readonly LatestRequestController _requests = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _stop;
    private readonly SelectionGestureDetector _gestures = new(4, 4);
    private readonly object _gestureGate = new();
    private readonly TimeSpan _pollInterval;
    private Configuration _configuration = new(false, true, 80, false);
    private MacInputEvent? _press;
    private ScreenPoint _pointer;
    private Task? _loop;
    private long _sequence;
    private long _sessionGeneration;
    private volatile bool _sessionActive = true;
    private int _needsConfiguration = 1;
    private volatile bool _mouseRunning;
    private volatile bool _hotkeyRunning;
    private volatile bool _disposed;
    private NativeServiceState _state = NativeServiceState.Unavailable;
    private string? _inputDiagnostic;
    private long _inputEvents;
    private long _selectionReads;
    private long _successfulSelections;
    private SelectionFailureKind _lastSelectionFailure;

    // The native helper filters the frontmost window; geometric UI checks also hit covered windows.
    public MacSelectionRuntime() : this(new MacHelperClient(), isMac: OperatingSystem.IsMacOS) { }

    public MacSelectionRuntime(Func<ScreenPoint, CancellationToken, Task<bool>> isOwnWindow)
        : this(new MacHelperClient(), isOwnWindow, OperatingSystem.IsMacOS) { }

    internal MacSelectionRuntime(IMacHelperClient helper,
        Func<ScreenPoint, CancellationToken, Task<bool>>? isOwnWindow = null, Func<bool>? isMac = null,
        TimeSpan? pollInterval = null)
    {
        _helper = helper;
        _isMac = isMac ?? (() => true);
        _reader = new MacSelectionAdapter(helper, _isMac);
        _isOwnWindow = isOwnWindow ?? ((_, _) => Task.FromResult(false));
        _stop = _shutdown.Token;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(120);
    }

    public event EventHandler<SelectionCapturedEventArgs>? SelectionCaptured;
    public event EventHandler<ScreenPoint>? ExternalPointerPressed;
    public event EventHandler? StatusChanged;
    public event EventHandler<bool>? SessionActivityChanged;
    public bool IsSessionActive => _sessionActive;
    public bool IsRunning => _mouseRunning;
    public bool IsHotkeyRunning => _hotkeyRunning;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isMac()) throw new PlatformNotSupportedException("macOS input is unavailable.");
        _loop ??= Task.Run(PollAsync);
    }

    public void Configure(bool isEnabled, bool useClipboardFallback, int selectionDelayMilliseconds,
        bool useWpsPdfCompatibility = true, bool useSelectionContext = false)
    {
        Volatile.Write(ref _configuration, new(isEnabled, useClipboardFallback,
            Math.Clamp(selectionDelayMilliseconds, 0, 2_000), useSelectionContext));
        CancelSelection();
    }

    public void RepairInputCapture() => Interlocked.Exchange(ref _needsConfiguration, 1);

    private async Task PollAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var configure = Interlocked.Exchange(ref _needsConfiguration, 0) != 0;
                var exchange = await _helper.SendAsync(configure ? "configureInput" : "pollInput",
                    cancellationToken: _stop, input: configure ? new(MouseEnabled: true) : null).ConfigureAwait(false);
                if (exchange is not { State: NativeServiceState.Available, Response.Status: "ok", Response.Input: { } input })
                {
                    SetStatus(exchange.State, false, false, exchange.DiagnosticCode);
                    CancelSelection();
                    _sequence = 0;
                    RepairInputCapture();
                    await Task.Delay(TimeSpan.FromSeconds(5), _stop).ConfigureAwait(false);
                    continue;
                }
                SetStatus(exchange.State, input.MouseRunning, input.HotkeyRunning,
                    exchange.Response.DiagnosticCode is { } code ? MacHelperProtocol.SafeDiagnostic(code) : null);
                if (exchange.Response.DiagnosticCode == "input-not-configured" || input.Sequence < _sequence)
                {
                    CancelSelection();
                    _sequence = 0;
                    RepairInputCapture();
                    UpdateSession(input);
                    await Task.Delay(_pollInterval, _stop).ConfigureAwait(false);
                    continue;
                }
                await HandleInputAsync(input, _stop).ConfigureAwait(false);
                await Task.Delay(_pollInterval, _stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (_disposed) { break; }
            catch
            {
                SetStatus(NativeServiceState.Unavailable, false, false, "mac-input-loop-failed");
                CancelSelection();
                RepairInputCapture();
                try { await Task.Delay(TimeSpan.FromSeconds(5), _stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    internal async Task HandleInputAsync(MacInputSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        UpdateSession(snapshot);
        if (!_sessionActive) return;
        var received = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var item in snapshot.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) return;
            if (item.Sequence <= _sequence) continue;
            _sequence = item.Sequence;
            Interlocked.Increment(ref _inputEvents);
            _pointer = item.Pointer;
            if (item.AgeMilliseconds + System.Diagnostics.Stopwatch.GetElapsedTime(received).TotalMilliseconds > 500)
            { CancelSelection(); continue; }
            if (item.Kind == MacInputKind.Cancel) { CancelSelection(); continue; }
            if (item.Kind == MacInputKind.TranslateClipboard) { TranslateClipboard(); continue; }
            if (await _isOwnWindow(item.Pointer, cancellationToken).ConfigureAwait(false)
                || item.AgeMilliseconds + System.Diagnostics.Stopwatch.GetElapsedTime(received).TotalMilliseconds > 500)
            { CancelSelection(); continue; }
            if (item.Kind == MacInputKind.PointerDown)
            {
                CancelSelection();
                try { ExternalPointerPressed?.Invoke(this, item.Pointer); } catch { }
                if (item.Modified || !Volatile.Read(ref _configuration).Enabled) continue;
                lock (_gestureGate) { _press = item; _gestures.Press(item.Pointer); }
                continue;
            }
            SelectionGesture? gesture;
            lock (_gestureGate)
            {
                var press = _press;
                _press = null;
                if (item.Modified || press is null || press.ForegroundProcessId != item.ForegroundProcessId
                    || press.ForegroundApplication != item.ForegroundApplication)
                { _gestures.Cancel(); continue; }
                gesture = _gestures.Release(item.Pointer, DateTimeOffset.UtcNow);
            }
            if (gesture is { } selected && Volatile.Read(ref _configuration).Enabled)
                _ = CaptureAsync(item, selected);
        }
    }

    private async Task CaptureAsync(MacInputEvent input, SelectionGesture gesture)
    {
        try
        {
            using var pending = _requests.Begin();
            var settings = Volatile.Read(ref _configuration);
            if (settings.Delay > 0) await Task.Delay(settings.Delay, pending.Token).ConfigureAwait(false);
            if (_disposed || !_sessionActive || !pending.IsCurrent || !settings.Enabled || settings != Volatile.Read(ref _configuration)) return;
            if (await _isOwnWindow(gesture.Start, pending.Token).ConfigureAwait(false)
                || await _isOwnWindow(gesture.End, pending.Token).ConfigureAwait(false)) return;
            var request = new SelectionRequest(SelectionTrigger.MouseGesture, gesture.End, input.ForegroundApplication,
                gesture.Bounds, settings.Context, input.ForegroundProcessId);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _selectionReads);
            var result = await _reader.ReadForInputAsync(request, settings.Copy, input.Sequence, pending.Token).ConfigureAwait(false);
            var duration = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (!_disposed && _sessionActive && pending.IsCurrent && settings.Enabled && settings == Volatile.Read(ref _configuration))
            {
                _lastSelectionFailure = result.Failure;
                if (result.Succeeded) Interlocked.Increment(ref _successfulSelections);
                SelectionCaptured?.Invoke(this, new(request, result, duration));
            }
        }
        catch { }
    }

    public void TranslateClipboard() => _ = TranslateClipboardAsync();

    private async Task TranslateClipboardAsync()
    {
        try
        {
            if (_disposed || !_sessionActive) return;
            using var pending = _requests.Begin();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var exchange = await _helper.SendAsync("readClipboard", cancellationToken: pending.Token).ConfigureAwait(false);
            var duration = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            var request = new SelectionRequest(SelectionTrigger.TranslateShortcut, exchange.Response?.Pointer ?? _pointer);
            var result = exchange.Response is { Status: "ok", Selection: { } selection } ? selection
                : SelectionResult.Failed(SelectionFailureKind.ClipboardUnavailable, exchange.DiagnosticCode);
            if (!_disposed && _sessionActive && pending.IsCurrent) SelectionCaptured?.Invoke(this, new(request, result, duration));
        }
        catch { }
    }

    private void CancelSelection()
    {
        _requests.Cancel();
        lock (_gestureGate) { _press = null; _gestures.Cancel(); }
    }

    private void UpdateSession(MacInputSnapshot snapshot)
    {
        if (_disposed) return;
        if (_sessionGeneration == snapshot.SessionGeneration && _sessionActive == snapshot.SessionActive) return;
        CancelSelection();
        _sessionGeneration = snapshot.SessionGeneration;
        // A missed sleep/wake pair still invalidates host requests and stale visible windows.
        if (_sessionActive)
        {
            _sessionActive = false;
            try { SessionActivityChanged?.Invoke(this, false); } catch { }
        }
        _sessionActive = snapshot.SessionActive;
        if (_sessionActive) { try { SessionActivityChanged?.Invoke(this, true); } catch { } }
    }

    private void SetStatus(NativeServiceState state, bool mouse, bool hotkey, string? diagnostic = null)
    {
        var changed = _state != state || _mouseRunning != mouse || _hotkeyRunning != hotkey || _inputDiagnostic != diagnostic;
        _state = state; _mouseRunning = mouse; _hotkeyRunning = hotkey;
        _inputDiagnostic = diagnostic;
        if (changed && !_disposed) { try { StatusChanged?.Invoke(this, EventArgs.Empty); } catch { } }
    }

    public string CreateDiagnostics(bool chinese) => $"macOS native input: {_state}; Mouse={IsRunning}; Hotkey={IsHotkeyRunning}; Session={IsSessionActive}; Protocol={MacHelperProtocol.Version}\n"
        + $"Input diagnostic: {_inputDiagnostic ?? "none"}; Events={Interlocked.Read(ref _inputEvents)}; Reads={Interlocked.Read(ref _selectionReads)}; Captured={Interlocked.Read(ref _successfulSelections)}; LastSelection={_lastSelectionFailure}";
    public Task<PermissionState> GetStateAsync(CancellationToken cancellationToken = default) => _reader.GetStateAsync(cancellationToken);
    public Task<PlatformPermissionStatus> GetStatusAsync(CancellationToken cancellationToken = default) => _reader.GetStatusAsync(cancellationToken);
    public async Task RequestAccessibilityPermissionAsync(CancellationToken cancellationToken = default)
    { await _reader.RequestAccessibilityPermissionAsync(cancellationToken).ConfigureAwait(false); RepairInputCapture(); }
    public Task OpenAccessibilitySettingsAsync(CancellationToken cancellationToken = default) => _reader.OpenAccessibilitySettingsAsync(cancellationToken);
    public async Task RequestInputMonitoringPermissionAsync(CancellationToken cancellationToken = default)
    { await _reader.RequestInputMonitoringPermissionAsync(cancellationToken).ConfigureAwait(false); RepairInputCapture(); }
    public Task OpenInputMonitoringSettingsAsync(CancellationToken cancellationToken = default) => _reader.OpenInputMonitoringSettingsAsync(cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _requests.Dispose();
        _reader.Dispose();
        _shutdown.Dispose();
        _mouseRunning = _hotkeyRunning = false;
    }

    private sealed record Configuration(bool Enabled, bool Copy, int Delay, bool Context);
}
