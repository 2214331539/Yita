using System.Diagnostics;
using Yita.Interop;
using Yita.Models;
using Yita.Services;

namespace Yita.Selection;

/// <summary>
/// Edge keeps its full renderer accessibility tree disabled until assistive
/// technology is detected. A short, reversible screen-reader notification
/// activates a dormant tree. Window-scoped refresh is allowed after a failed
/// read because scrolling, zooming, and renderer replacement invalidate the
/// assumption that activation succeeds once for an entire browser process.
/// </summary>
internal static class EdgeAccessibilityActivator
{
    private const int ActivationPulseMilliseconds = 100;
    private static readonly EdgeAccessibilityRefreshGate RefreshGate = new();
    private static readonly object ActivationSyncRoot = new();
    private static long _activationCount;

    internal static long ActivationCount => Interlocked.Read(ref _activationCount);

    internal static bool ActivateIfNeeded(
        ScreenPoint point,
        CancellationToken cancellationToken,
        bool refresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processId = WindowProcessResolver.TryGetExternalProcessIdAt(point);
        if (processId is not { } targetProcessId
            || !ShouldActivateProcessName(TryGetProcessName(targetProcessId)))
        {
            return false;
        }

        var hitWindow = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint { X = point.X, Y = point.Y });
        var targetWindow = NativeMethods.GetAncestor(hitWindow, NativeMethods.GaRoot);
        if (targetWindow == IntPtr.Zero || !Monitor.TryEnter(ActivationSyncRoot))
        {
            return false;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!RefreshGate.TryEnter(targetProcessId, targetWindow, Environment.TickCount64, refresh))
            {
                return false;
            }

            var originalScreenReaderState = 0;
            if (!NativeMethods.SystemParametersInfoGet(
                    NativeMethods.SpiGetScreenReader,
                    0,
                    ref originalScreenReaderState,
                    0))
            {
                return false;
            }

            if (originalScreenReaderState != 0)
            {
                return false;
            }

            var activated = NativeMethods.SystemParametersInfoSet(
                NativeMethods.SpiSetScreenReader,
                1,
                IntPtr.Zero,
                NativeMethods.SpifSendChange);
            if (!activated)
            {
                return false;
            }

            try
            {
                if (cancellationToken.WaitHandle.WaitOne(ActivationPulseMilliseconds))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                Interlocked.Increment(ref _activationCount);
            }
            finally
            {
                // Restore the exact state observed before our short activation.
                _ = NativeMethods.SystemParametersInfoSet(
                    NativeMethods.SpiSetScreenReader,
                    0,
                    IntPtr.Zero,
                    NativeMethods.SpifSendChange);
            }

            return true;
        }
        finally
        {
            Monitor.Exit(ActivationSyncRoot);
        }
    }

    internal static bool ShouldActivateProcessName(string? processName)
    {
        return string.Equals(processName, "msedge", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}

internal sealed class EdgeAccessibilityRefreshGate
{
    internal const int ActivationLifetimeMilliseconds = 15_000;
    internal const int RecoveryCooldownMilliseconds = 250;
    internal const int MaximumTrackedWindows = 64;
    private readonly Dictionary<(uint ProcessId, IntPtr Window), long> _lastActivation = new();
    private readonly object _sync = new();

    internal bool TryEnter(uint processId, IntPtr window, long now, bool refresh)
    {
        lock (_sync)
        {
            var key = (processId, window);
            var cooldown = refresh ? RecoveryCooldownMilliseconds : ActivationLifetimeMilliseconds;
            if (_lastActivation.TryGetValue(key, out var last)
                && now >= last && now - last < cooldown)
            {
                return false;
            }

            if (_lastActivation.Count >= MaximumTrackedWindows && !_lastActivation.ContainsKey(key))
            {
                _lastActivation.Remove(_lastActivation.MinBy(entry => entry.Value).Key);
            }

            _lastActivation[key] = now;
            return true;
        }
    }
}
