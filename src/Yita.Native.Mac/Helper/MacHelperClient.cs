using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Yita.Core.Selection;

namespace Yita.Native.Mac;

internal sealed class MacHelperClient : IMacHelperClient
{
    private readonly Func<ProcessStartInfo?> _startInfo;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _stop;
    private readonly object _processGate = new();
    private readonly Queue<long> _starts = new();
    private Worker? _worker;
    private Task? _clipboardCleanup;
    private volatile bool _disposed;

    internal MacHelperClient() : this(FindStartInfo) { }
    internal MacHelperClient(Func<ProcessStartInfo?> startInfo, TimeSpan? requestTimeout = null, TimeProvider? time = null)
    {
        _startInfo = startInfo;
        _stop = _shutdown.Token;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(3);
        _time = time ?? TimeProvider.System;
    }

    public async Task<MacHelperExchange> SendAsync(string command, SelectionRequest? selection = null,
        CancellationToken cancellationToken = default, bool allowClipboardFallback = false, MacInputOptions? input = null)
    {
        if (command is not ("permissions" or "requestAccessibility" or "openAccessibilitySettings" or "readSelection"
            or "requestInputMonitoring" or "openInputMonitoringSettings" or "configureInput" or "pollInput" or "readClipboard"))
            throw new ArgumentException("Unsupported helper command.", nameof(command));
        if (allowClipboardFallback && command != "readSelection") throw new ArgumentException("Clipboard fallback requires a selection request.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop);
        deadline.CancelAfter(_requestTimeout);
        var acquired = false;
        Worker? worker = null;
        string? id = null;
        Task<byte[]>? responseRead = null;
        using var responseStop = new CancellationTokenSource();
        var completed = false;
        try
        {
            await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            worker = await EnsureWorkerAsync(deadline.Token).ConfigureAwait(false);
            id = Guid.NewGuid().ToString("N");
            var request = JsonSerializer.SerializeToUtf8Bytes(new MacHelperRequest(MacHelperProtocol.Version, id, command, selection, allowClipboardFallback, input),
                MacHelperProtocol.JsonOptions);
            if (request.Length > MacHelperProtocol.MaximumRequestBytes) throw new ArgumentException("Helper request exceeded the limit.");
            deadline.Token.ThrowIfCancellationRequested();
            if (allowClipboardFallback)
            {
                lock (_processGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    worker.ActiveClipboardRequestId = id;
                }
            }
            // Once a clipboard request starts, send a whole frame before notifying cancellation.
            using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stop);
            writeDeadline.CancelAfter(TimeSpan.FromSeconds(2));
            var writeToken = allowClipboardFallback ? writeDeadline.Token : deadline.Token;
            await worker.Process.StandardInput.BaseStream.WriteAsync(request, writeToken).ConfigureAwait(false);
            await worker.Process.StandardInput.BaseStream.WriteAsync(new byte[] { (byte)'\n' }, writeToken).ConfigureAwait(false);
            await worker.Process.StandardInput.BaseStream.FlushAsync(writeToken).ConfigureAwait(false);
            // Wait cancellation leaves the same bounded frame read alive for cleanup.
            responseRead = worker.Reader.ReadAsync(responseStop.Token);
            var response = MacHelperProtocol.Parse(await responseRead.WaitAsync(deadline.Token).ConfigureAwait(false));
            MacHelperProtocol.ValidateResponse(response, id, command, allowClipboardFallback);
            completed = true;
            return new MacHelperExchange(NativeServiceState.Available, response,
                response.Status == "error" ? MacHelperProtocol.SafeDiagnostic(response.DiagnosticCode) : null);
        }
        catch (OperationCanceledException)
        {
            if (acquired)
            {
                if (!_disposed && allowClipboardFallback && worker is not null && id is not null && responseRead is not null)
                    completed = await CancelClipboardRequestAsync(worker, id, responseRead).ConfigureAwait(false);
                if (!completed) StopWorker();
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new MacHelperExchange(_disposed ? NativeServiceState.Unavailable : NativeServiceState.Timeout,
                DiagnosticCode: _disposed ? "mac-helper-stopped" : "mac-helper-timeout");
        }
        catch (MacHelperProtocolException exception)
        {
            if (acquired) StopWorker();
            return new MacHelperExchange(exception.State, DiagnosticCode: exception.State switch
            {
                NativeServiceState.Missing => "mac-helper-missing",
                NativeServiceState.RestartBackoff => "mac-helper-restart-backoff",
                NativeServiceState.ProtocolMismatch => "mac-helper-version-mismatch",
                _ => "mac-helper-invalid-response",
            });
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or UnauthorizedAccessException
            or InvalidOperationException or ObjectDisposedException)
        {
            if (acquired) StopWorker();
            cancellationToken.ThrowIfCancellationRequested();
            return new MacHelperExchange(NativeServiceState.Unavailable, DiagnosticCode: "mac-helper-unavailable");
        }
        finally
        {
            if (completed && worker is not null)
            {
                lock (_processGate) { worker.ActiveClipboardRequestId = null; }
            }
            responseStop.Cancel();
            if (responseRead is not null) _ = ObserveReadAsync(responseRead);
            if (acquired) _requests.Release();
        }
    }

    private static async Task<bool> CancelClipboardRequestAsync(Worker worker, string id, Task<byte[]> responseRead)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var request = JsonSerializer.SerializeToUtf8Bytes(new MacHelperRequest(MacHelperProtocol.Version, id, "cancelSelection"), MacHelperProtocol.JsonOptions);
            await worker.Process.StandardInput.BaseStream.WriteAsync(request, cleanup.Token).ConfigureAwait(false);
            await worker.Process.StandardInput.BaseStream.WriteAsync(new byte[] { (byte)'\n' }, cleanup.Token).ConfigureAwait(false);
            await worker.Process.StandardInput.BaseStream.FlushAsync(cleanup.Token).ConfigureAwait(false);
            var response = MacHelperProtocol.Parse(await responseRead.WaitAsync(cleanup.Token).ConfigureAwait(false));
            MacHelperProtocol.ValidateResponse(response, id, "readSelection", allowClipboardFallback: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or InvalidOperationException or Win32Exception)
        { return false; }
    }

    private static async Task ObserveReadAsync(Task<byte[]> task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task<Worker> EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        Task? cleanup;
        lock (_processGate) { cleanup = _clipboardCleanup; }
        if (cleanup is not null) await cleanup.WaitAsync(cancellationToken).ConfigureAwait(false);
        Worker worker;
        lock (_processGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is { Process.HasExited: false } active) return active;
            StopWorker();
            var now = _time.GetTimestamp();
            while (_starts.TryPeek(out var start) && _time.GetElapsedTime(start, now) >= TimeSpan.FromSeconds(30)) _starts.Dequeue();
            if (_starts.Count >= 3) throw new MacHelperProtocolException(NativeServiceState.RestartBackoff);
            var info = _startInfo() ?? throw new MacHelperProtocolException(NativeServiceState.Missing);
            _starts.Enqueue(now);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
            info.ArgumentList.Add("--parent-pid");
            info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            var process = Process.Start(info) ?? throw new IOException("Could not start native helper.");
            worker = new Worker(process, new BoundedJsonLineReader(process.StandardOutput.BaseStream, MacHelperProtocol.MaximumResponseBytes));
            _worker = worker;
            _ = DrainErrorAsync(process.StandardError.BaseStream);
        }
        var ready = MacHelperProtocol.Parse(await worker.Reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        MacHelperProtocol.ValidateReady(ready, worker.Process.Id);
        return worker;
    }

    private static async Task DrainErrorAsync(Stream stream)
    {
        try
        {
            var buffer = new byte[4096];
            while (await stream.ReadAsync(buffer).ConfigureAwait(false) != 0) { }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    private void StopWorker()
    {
        Worker? worker;
        lock (_processGate)
        {
            worker = _worker;
            _worker = null;
            if (worker is null) return;
            if (worker.ActiveClipboardRequestId is not null)
            {
                _clipboardCleanup = FinishClipboardWorkerAsync(worker);
                return;
            }
        }
        KillWorker(worker);
    }

    private static async Task FinishClipboardWorkerAsync(Worker worker)
    {
        try
        {
            worker.Process.StandardInput.Close();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await worker.Process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or InvalidOperationException or Win32Exception) { }
        finally { KillWorker(worker); }
    }

    private static void KillWorker(Worker worker)
    {
        try { if (!worker.Process.HasExited) worker.Process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
        finally { worker.Process.Dispose(); }
    }

    private static ProcessStartInfo? FindStartInfo()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var path = ResolveHelperPath(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("YITA_MAC_HELPER_PATH"));
        return File.Exists(path) ? new ProcessStartInfo(path) : null;
    }

    internal static string ResolveHelperPath(string baseDirectory, string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var contents = Path.GetDirectoryName(directory);
        var app = contents is null ? null : Path.GetDirectoryName(contents);
        // Installed bundles keep nested executables outside the managed runtime directory.
        var root = Path.GetFileName(directory) == "MacOS" && Path.GetFileName(contents) == "Contents"
            && app?.EndsWith(".app", StringComparison.Ordinal) == true
            ? Path.Combine(contents!, "Helpers") : directory;
        return Path.Combine(root, "Yita.Native.Mac.Helper.app", "Contents", "MacOS", "Yita.Native.Mac.Helper");
    }

    public void Dispose()
    {
        lock (_processGate)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            StopWorker();
            _shutdown.Dispose();
        }
        // Pending exchanges may still hold the managed semaphore.
    }

    private sealed record Worker(Process Process, BoundedJsonLineReader Reader)
    {
        internal string? ActiveClipboardRequestId { get; set; }
    }
}
