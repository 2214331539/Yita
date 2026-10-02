using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Yita.Core.Platform;

/// <summary>Holds a process-lifetime file lock and receives bounded, user-local activation messages.</summary>
public sealed class PortableSingleInstanceGuard : ISingleInstanceGuard
{
    private readonly object _gate = new();
    private readonly FileStream? _lockFile;
    private readonly NamedPipeServerStream? _server;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listener;
    private Action? _openSettings;
    private bool _pendingActivation;
    private volatile bool _disposed;

    public PortableSingleInstanceGuard(string lockPath, bool requestActivation = true)
    {
        var path = Path.GetFullPath(lockPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var identity = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        var pipeName = "Yita.Activation." + hash;
        try
        {
            _lockFile = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
        {
            if (requestActivation) RequestActivation(pipeName);
            return;
        }

        try
        {
            _server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            IsOwner = true;
            _listener = ListenAsync(_shutdown.Token);
        }
        catch
        {
            _lockFile.Dispose();
            _shutdown.Dispose();
            throw;
        }
    }

    public bool IsOwner { get; }

    private static void RequestActivation(string pipeName)
    {
        try
        {
            SendActivationAsync(pipeName).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            Trace.TraceWarning("Yita activation unavailable: {0}", exception.GetType().Name);
        }
    }

    private static async Task SendActivationAsync(string pipeName)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await client.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
    }

    public void StartActivationListener(Action openSettings)
    {
        ArgumentNullException.ThrowIfNull(openSettings);
        bool pending;
        lock (_gate)
        {
            if (_disposed || !IsOwner || _openSettings is not null) return;
            _openSettings = openSettings;
            pending = _pendingActivation;
            _pendingActivation = false;
        }
        if (pending) InvokeActivation(openSettings);
    }

    private void DispatchActivation()
    {
        Action? openSettings;
        lock (_gate)
        {
            if (_disposed) return;
            openSettings = _openSettings;
            if (openSettings is null) _pendingActivation = true;
        }
        if (openSettings is not null) InvokeActivation(openSettings);
    }

    private void InvokeActivation(Action openSettings)
    {
        if (_disposed) return;
        try { openSettings(); }
        catch (Exception exception) { Trace.TraceWarning("Yita activation callback failed: {0}", exception.GetType().Name); }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        var message = new byte[1];
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _server!.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                var count = await _server.ReadAsync(message, readTimeout.Token).ConfigureAwait(false);
                if (count == 1 && message[0] == 1 && !_disposed)
                    DispatchActivation();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) when (_disposed) { break; }
            catch (IOException) when (_disposed) { break; }
            catch (IOException exception)
            {
                Trace.TraceWarning("Yita activation listener failed: {0}", exception.GetType().Name);
                break;
            }
            finally
            {
                try { if (_server!.IsConnected) _server.Disconnect(); }
                catch (Exception exception) when (exception is ObjectDisposedException or IOException) { }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            _server?.Dispose();
            // Keep the lock file: unlinking it could let two owners lock different inodes.
            _lockFile?.Dispose();
            if (_listener is null) _shutdown.Dispose();
            else _ = _listener.ContinueWith(_ => _shutdown.Dispose(), TaskScheduler.Default);
        }
    }
}
