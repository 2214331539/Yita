using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Yita.Models;
using Yita.Services;

namespace Yita.Selection;

internal sealed class IsolatedUiaSelectionReader : IContextualSelectionReader, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<ProcessStartInfo> _startInfo;
    private readonly Func<ScreenPoint, uint?> _targetPid;
    private readonly bool _activateAccessibility;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, long> _failedTargets = new();
    private Process? _worker;
    private long _failures, _successes, _empty;
    private int _active;

    internal IsolatedUiaSelectionReader(Func<ProcessStartInfo>? startInfo = null,
        Func<ScreenPoint, uint?>? targetPid = null)
    {
        _startInfo = startInfo ?? CreateStartInfo;
        _targetPid = targetPid ?? WindowProcessResolver.TryGetExternalProcessIdAt;
        _activateAccessibility = startInfo is null;
    }

    internal static ProcessStartInfo CreateStartInfo()
    {
        var assemblyPath = typeof(IsolatedUiaSelectionReader).Assembly.Location;
        var executable = Path.ChangeExtension(assemblyPath, ".exe");
        var info = new ProcessStartInfo(executable);
        if (!File.Exists(executable))
        {
            // Framework-dependent/test layouts can lack an apphost.
            info.FileName = Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "", "dotnet.exe");
            info.ArgumentList.Add(assemblyPath);
        }
        info.ArgumentList.Add("--selection-worker");
        info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return info;
    }

    public string CreateStatusReport(bool useChinese) => useChinese
        ? $"独立进程取词\n正在读取：{Volatile.Read(ref _active)}\n成功：{Interlocked.Read(ref _successes)} / 无选区：{Interlocked.Read(ref _empty)} / 子进程失败或超时：{Interlocked.Read(ref _failures)}"
        : $"Isolated selection reader\nActive: {Volatile.Read(ref _active)}\nSuccess: {Interlocked.Read(ref _successes)} / empty: {Interlocked.Read(ref _empty)} / worker failures or timeouts: {Interlocked.Read(ref _failures)}";

    internal bool RequiresCompatibilityAt(ScreenPoint point)
    {
        var pid = _targetPid(point);
        if (pid is not { } id || !_failedTargets.TryGetValue(id, out var until)) return false;
        if (Environment.TickCount64 < until) return true;
        _failedTargets.TryRemove(id, out _);
        return false;
    }

    internal void MarkUnresponsive(ScreenPoint point)
    {
        if (_targetPid(point) is { } id) _failedTargets[id] = Environment.TickCount64 + 60_000;
    }

    internal async Task WarmUpAsync()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try { await EnsureWorkerAsync(deadline.Token).ConfigureAwait(false); }
            catch (Exception e) when (IsOperationalFailure(e) || e is OperationCanceledException) { StopWorker(); }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { }
    }

    public async Task<string?> TryReadSelectedTextAsync(ScreenPoint point, CancellationToken cancellationToken) =>
        (await TryReadSelectionAsync(point, false, cancellationToken).ConfigureAwait(false))?.Text;

    public async Task<SelectionCapture?> TryReadSelectionAsync(ScreenPoint point, bool includeContext, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var token = deadline.Token;
        token.ThrowIfCancellationRequested();
        if (RequiresCompatibilityAt(point)) return null;
        var pid = _targetPid(point);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        Interlocked.Increment(ref _active);
        try
        {
            // Keep the reversible system accessibility pulse in the parent:
            // killing a stuck helper must never prevent its finally restoring it.
            if (_activateAccessibility)
                await Task.Run(() => EdgeAccessibilityActivator.ActivateIfNeeded(point, token), token).ConfigureAwait(false);
            await EnsureWorkerAsync(token).ConfigureAwait(false);
            var response = await ReadWorkerAsync(point, includeContext, token).ConfigureAwait(false);
            if (response.Capture is null && _activateAccessibility
                && await Task.Run(() => EdgeAccessibilityActivator.ActivateIfNeeded(point, token, refresh: true), token).ConfigureAwait(false))
            {
                // Preserve the existing recovery after a browser replaces its
                // accessibility tree during scrolling/zooming. UIA stays remote.
                response = await ReadWorkerAsync(point, includeContext, token).ConfigureAwait(false);
            }
            if (string.IsNullOrWhiteSpace(response.Capture?.Text)) Interlocked.Increment(ref _empty);
            else Interlocked.Increment(ref _successes);
            return response.Capture;
        }
        catch (Exception e) when (IsOperationalFailure(e) || e is OperationCanceledException)
        {
            Interlocked.Increment(ref _failures);
            // Temporarily bypass the failing provider; native/guarded clipboard
            // readers can still work for this process, including this gesture.
            if (pid is { } id) _failedTargets[id] = Environment.TickCount64 + 60_000;
            new RuntimeHealthJournal().Record(RuntimeHealthEvent.SelectionWorkerFailed,
                numericCode: _worker is { HasExited: true } ? _worker.ExitCode : -1);
            StopWorker();
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        finally { Interlocked.Decrement(ref _active); _gate.Release(); }
    }

    private async Task<SelectionWorkerResponse> ReadWorkerAsync(ScreenPoint point, bool includeContext, CancellationToken token)
    {
        var request = JsonSerializer.Serialize(new SelectionWorkerRequest(point, includeContext));
        await _worker!.StandardInput.WriteLineAsync(request.AsMemory(), token).ConfigureAwait(false);
        await _worker.StandardInput.FlushAsync(token).ConfigureAwait(false);
        var line = await _worker.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
        if (line is null || line.Length > 256_000) throw new IOException("Selection worker stopped.");
        var response = JsonSerializer.Deserialize<SelectionWorkerResponse>(line);
        if (response is null || response.Failed) throw new IOException("Selection worker failed.");
        return response;
    }

    private async Task EnsureWorkerAsync(CancellationToken token)
    {
        if (_worker is { HasExited: false }) return;
        StopWorker();
        var info = _startInfo();
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.WindowStyle = ProcessWindowStyle.Hidden;
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        info.StandardInputEncoding = info.StandardOutputEncoding = new UTF8Encoding(false);
        _worker = Process.Start(info) ?? throw new IOException("Cannot start selection worker.");
        // Drain without retaining native crash output, which may contain paths.
        _worker.ErrorDataReceived += (_, _) => { };
        _worker.BeginErrorReadLine();
        if (await _worker.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) != SelectionWorker.Ready)
            throw new IOException("Invalid selection worker handshake.");
    }

    private void StopWorker()
    {
        var worker = _worker;
        _worker = null;
        if (worker is null) return;
        try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
        finally { worker.Dispose(); }
    }

    private static bool IsOperationalFailure(Exception e) =>
        e is IOException or Win32Exception or InvalidOperationException or JsonException;

    public void Dispose()
    {
        _lifetime.Cancel();
        // Serialize teardown with reads; no UI-thread wait on an untrusted provider.
        _ = StopAfterReadAsync();
    }

    private async Task StopAfterReadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { StopWorker(); }
        finally { _gate.Release(); }
    }
}
