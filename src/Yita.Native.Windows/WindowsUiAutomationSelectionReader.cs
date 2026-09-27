using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Yita.Core.Selection;

namespace Yita.Native.Windows;

/// <summary>
/// Reads a selection through a separate Windows UI Automation worker.
///
/// UI Automation providers are supplied by the target application and may
/// block or throw outside of the caller's control. Keeping the process
/// boundary here prevents a broken WPS/browser provider from terminating the
/// Avalonia process. A missing or unhealthy worker is a normal fallback case.
/// </summary>
public sealed class WindowsUiAutomationSelectionReader : ISelectionReader
{
    private readonly IWindowsUiAutomationWorker _worker;

    public WindowsUiAutomationSelectionReader()
        : this(new WindowsUiAutomationWorkerClient())
    {
    }

    public WindowsUiAutomationSelectionReader(IWindowsUiAutomationWorker worker)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
    }

    public Task<SelectionResult> ReadAsync(
        SelectionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(
                SelectionResult.Failed(SelectionFailureKind.UnsupportedApplication, "windows-only"));
        }

        return _worker.ReadAsync(request, cancellationToken);
    }
}

public interface IWindowsUiAutomationWorker : IDisposable
{
    bool IsAvailable { get; }

    Task<SelectionResult> ReadAsync(
        SelectionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// JSON-lines client for the standalone Yita UI Automation worker.
/// The worker path can be overridden with YITA_UIA_WORKER_PATH for development
/// and packaging experiments. The default path is a sibling executable.
/// </summary>
public sealed class WindowsUiAutomationWorkerClient : IWindowsUiAutomationWorker
{
    private const string ReadyLine = "YITA-UIA-1";
    private const int MaximumWireLineLength = 256_000;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(1_200);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string?> _workerPath;
    private readonly object _processGate = new();
    private Process? _worker;
    private bool _disposed;

    public WindowsUiAutomationWorkerClient()
        : this(FindWorkerPath)
    {
    }

    internal WindowsUiAutomationWorkerClient(Func<string?> workerPath)
    {
        _workerPath = workerPath ?? throw new ArgumentNullException(nameof(workerPath));
    }

    public bool IsAvailable => OperatingSystem.IsWindows() && _workerPath() is not null;

    public async Task<SelectionResult> ReadAsync(
        SelectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindows())
        {
            return SelectionResult.Failed(SelectionFailureKind.UnsupportedApplication, "windows-only");
        }

        if (_workerPath() is null)
        {
            return SelectionResult.Failed(SelectionFailureKind.UnsupportedApplication, "uia-worker-missing");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(RequestTimeout);
            await EnsureWorkerAsync(deadline.Token).ConfigureAwait(false);

            var line = JsonSerializer.Serialize(new WorkerRequest(
                request.Pointer.X,
                request.Pointer.Y,
                request.Trigger.ToString()));
            await _worker!.StandardInput.WriteLineAsync(line.AsMemory(), deadline.Token).ConfigureAwait(false);
            await _worker.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);

            var responseLine = await _worker.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            if (responseLine is null || responseLine.Length > MaximumWireLineLength)
            {
                throw new IOException("UI Automation worker stopped.");
            }

            var response = JsonSerializer.Deserialize<WorkerResponse>(responseLine);
            if (response is null)
            {
                throw new IOException("Invalid UI Automation worker response.");
            }

            return response.ToSelectionResult(request);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            StopWorker();
            return SelectionResult.Failed(SelectionFailureKind.Timeout, "uia-worker-timeout");
        }
        catch (Exception exception) when (exception is IOException
            or InvalidOperationException
            or JsonException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException
            or ObjectDisposedException)
        {
            StopWorker();
            return SelectionResult.Failed(SelectionFailureKind.Unknown, "uia-worker-failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        lock (_processGate)
        {
            if (_worker is { HasExited: false }) return;
            StopWorker();

            var path = _workerPath();
            if (path is null) throw new FileNotFoundException("Yita UI Automation worker was not found.");

            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            info.ArgumentList.Add("--parent-pid");
            info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _worker = Process.Start(info) ?? throw new IOException("Cannot start UI Automation worker.");
            _worker.ErrorDataReceived += static (_, _) => { };
            _worker.BeginErrorReadLine();
        }

        var handshake = await _worker!.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(handshake, ReadyLine, StringComparison.Ordinal))
        {
            StopWorker();
            throw new IOException("Invalid UI Automation worker handshake.");
        }
    }

    private void StopWorker()
    {
        Process? worker;
        lock (_processGate)
        {
            worker = _worker;
            _worker = null;
        }

        if (worker is null) return;
        try
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            worker.Dispose();
        }
    }

    private static string? FindWorkerPath()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var configured = Environment.GetEnvironmentVariable("YITA_UIA_WORKER_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var baseDirectory = AppContext.BaseDirectory;
        foreach (var name in new[] { "Yita.UIA.Worker.exe", "Yita.Native.Windows.UIA.Worker.exe" })
        {
            var candidate = Path.Combine(baseDirectory, name);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWorker();
        _gate.Dispose();
    }

    private sealed record WorkerRequest(double X, double Y, string Trigger);

    private sealed record WorkerResponse(
        string? Text,
        SelectionFailureKind Failure,
        string? DiagnosticCode,
        double? BoundsX,
        double? BoundsY,
        double? BoundsWidth,
        double? BoundsHeight)
    {
        internal SelectionResult ToSelectionResult(SelectionRequest request)
        {
            SelectionBounds? bounds = BoundsX is { } x
                && BoundsY is { } y
                && BoundsWidth is { } width
                && BoundsHeight is { } height
                ? new SelectionBounds(x, y, width, height)
                : new SelectionBounds(request.Pointer.X, request.Pointer.Y, 0, 0);

            return new SelectionResult(
                string.IsNullOrWhiteSpace(Text) ? null : Text.Trim(),
                SelectionSource.Accessibility,
                bounds,
                Failure,
                DiagnosticCode);
        }
    }
}
