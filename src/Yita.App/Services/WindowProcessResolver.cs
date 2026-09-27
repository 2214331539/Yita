using System.Diagnostics;
using Yita.Interop;
using Yita.Models;

namespace Yita.Services;

internal static class WindowProcessResolver
{
    private static readonly HashSet<string> ClipboardFallbackBlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd",
        "conhost",
        "powershell",
        "pwsh",
        "WindowsTerminal",
        "OpenConsole",
        "mintty",
        "alacritty",
        "wezterm",
    };

    private static readonly HashSet<string> DelayedAccessibilitySelectionProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        // Chromium and Electron commit selections from their render layer after
        // mouse-up in some document viewers. Retrying UIA/native reads is safe
        // and avoids enabling the clipboard compatibility path by default.
        "msedge",
        "chrome",
        "chromium",
        "msedgewebview2",
        "zotero",
    };

    public static bool IsCurrentProcessAt(ScreenPoint point)
    {
        var windowHandle = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint
        {
            X = point.X,
            Y = point.Y,
        });
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    public static uint? TryGetExternalProcessIdAt(ScreenPoint point)
    {
        var windowHandle = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint
        {
            X = point.X,
            Y = point.Y,
        });
        if (windowHandle == IntPtr.Zero)
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        return processId == 0 || processId == (uint)Environment.ProcessId
            ? null
            : processId;
    }

    public static bool ArePointsInSameExternalWindow(ScreenPoint first, ScreenPoint second)
    {
        return TryGetSelectionTargetAt(first, out var firstTarget)
            && TryGetSelectionTargetAt(second, out var secondTarget)
            && AreTargetsInSameExternalWindow(
                firstTarget,
                secondTarget,
                (uint)Environment.ProcessId);
    }

    internal static bool AreTargetsInSameExternalWindow(
        SelectionWindowTarget first,
        SelectionWindowTarget second,
        uint currentProcessId)
    {
        return first.RootOwnerHandle != IntPtr.Zero
            && first.RootOwnerHandle == second.RootOwnerHandle
            && first.RootOwnerProcessId != 0
            && first.RootOwnerProcessId != currentProcessId
            && second.RootOwnerProcessId != 0
            && second.RootOwnerProcessId != currentProcessId;
    }

    public static bool IsClipboardFallbackAllowedAt(ScreenPoint point)
    {
        var windowHandle = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint
        {
            X = point.X,
            Y = point.Y,
        });

        if (!IsExternalClipboardTarget(windowHandle))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return IsClipboardFallbackProcessAllowed(process.ProcessName);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static bool IsWpsPdfAt(ScreenPoint point)
    {
        var hit = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint { X = point.X, Y = point.Y });
        if (!IsExternalClipboardTarget(hit)) return false;
        var root = NativeMethods.GetAncestor(hit, NativeMethods.GaRootOwner);
        if (root == IntPtr.Zero || !Yita.Selection.CopyShortcut.IsTargetCurrent(hit)) return false;
        NativeMethods.GetWindowThreadProcessId(root, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var title = new System.Text.StringBuilder(1024);
            NativeMethods.GetWindowText(root, title, title.Capacity);
            if (IsWpsPdfProcess(process.ProcessName, title.ToString())) return true;
            // Some WPS frames omit the document extension, while their embedded
            // PDF renderer is still identified by its dedicated executable.
            NativeMethods.GetWindowThreadProcessId(hit, out var hitProcessId);
            using var hitProcess = Process.GetProcessById((int)hitProcessId);
            return IsWpsPdfProcess(hitProcess.ProcessName, title.ToString());
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal static bool IsWpsPdfProcess(string processName, string title) =>
        processName.Equals("wpspdf", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("kpdf", StringComparison.OrdinalIgnoreCase)
        || (processName.Equals("wps", StringComparison.OrdinalIgnoreCase)
            && title.Contains(".pdf", StringComparison.OrdinalIgnoreCase));

    public static bool RequiresSelectionStabilizationAt(ScreenPoint point)
    {
        var windowHandle = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint
        {
            X = point.X,
            Y = point.Y,
        });
        if (!IsExternalClipboardTarget(windowHandle))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return RequiresSelectionStabilization(process.ProcessName);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal static bool IsClipboardFallbackProcessAllowed(string? processName)
    {
        return !string.IsNullOrWhiteSpace(processName)
            && !ClipboardFallbackBlockedProcesses.Contains(processName);
    }

    internal static bool RequiresSelectionStabilization(string? processName)
    {
        return !string.IsNullOrWhiteSpace(processName)
            && DelayedAccessibilitySelectionProcesses.Contains(processName);
    }

    private static bool TryGetSelectionTargetAt(
        ScreenPoint point,
        out SelectionWindowTarget target)
    {
        target = default;
        var windowHandle = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint
        {
            X = point.X,
            Y = point.Y,
        });
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        var rootOwnerHandle = NativeMethods.GetAncestor(windowHandle, NativeMethods.GaRootOwner);
        if (rootOwnerHandle == IntPtr.Zero)
        {
            rootOwnerHandle = NativeMethods.GetAncestor(windowHandle, NativeMethods.GaRoot);
        }

        if (rootOwnerHandle == IntPtr.Zero)
        {
            rootOwnerHandle = windowHandle;
        }

        NativeMethods.GetWindowThreadProcessId(rootOwnerHandle, out var processId);
        target = new SelectionWindowTarget(rootOwnerHandle, processId);
        return processId != 0 && processId != (uint)Environment.ProcessId;
    }

    private static bool IsExternalClipboardTarget(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        return processId != 0 && processId != (uint)Environment.ProcessId;
    }
}

internal readonly record struct SelectionWindowTarget(
    IntPtr RootOwnerHandle,
    uint RootOwnerProcessId);
