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
        CancellationToken cancellationToken = default)
    {
        if (command is not ("permissions" or "requestAccessibility" or "openAccessibilitySettings" or "readSelection"))
            throw new ArgumentException("Unsupported helper command.", nameof(command));
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop);
        deadline.CancelAfter(_requestTimeout);
        var acquired = false;
        try
        {
            await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            var worker = await EnsureWorkerAsync(deadline.Token).ConfigureAwait(false);
            var id = Guid.NewGuid().ToString("N");
            var request = JsonSerializer.SerializeToUtf8Bytes(new MacHelperRequest(MacHelperProtocol.Version, id, command, selection),
                MacHelperProtocol.JsonOptions);
            if (request.Length > MacHelperProtocol.MaximumRequestBytes) throw new ArgumentException("Helper request exceeded the limit.");
            await worker.Process.StandardInput.BaseStream.WriteAsync(request, deadline.Token).ConfigureAwait(false);
            await worker.Process.StandardInput.BaseStream.WriteAsync(new byte[] { (byte)'\n' }, deadline.Token).ConfigureAwait(false);
            await worker.Process.StandardInput.BaseStream.FlushAsync(deadline.Token).ConfigureAwait(false);
            var response = MacHelperProtocol.Parse(await worker.Reader.ReadAsync(deadline.Token).ConfigureAwait(false));
            MacHelperProtocol.ValidateResponse(response, id, command);
            return new MacHelperExchange(NativeServiceState.Available, response,
                response.Status == "error" ? MacHelperProtocol.SafeDiagnostic(response.DiagnosticCode) : null);
        }
        catch (OperationCanceledException)
        {
            if (acquired) StopWorker();
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
        finally { if (acquired) _requests.Release(); }
    }

    private async Task<Worker> EnsureWorkerAsync(CancellationToken cancellationToken)
    {
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
        lock (_processGate) { worker = _worker; _worker = null; }
        if (worker is null) return;
        try { if (!worker.Process.HasExited) worker.Process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
        finally { worker.Process.Dispose(); }
    }

    private static ProcessStartInfo? FindStartInfo()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var configured = Environment.GetEnvironmentVariable("YITA_MAC_HELPER_PATH");
        var path = !string.IsNullOrWhiteSpace(configured) ? Path.GetFullPath(configured) : Path.Combine(
            AppContext.BaseDirectory, "Yita.Native.Mac.Helper.app", "Contents", "MacOS", "Yita.Native.Mac.Helper");
        return File.Exists(path) ? new ProcessStartInfo(path) : null;
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

    private sealed record Worker(Process Process, BoundedJsonLineReader Reader);
}
