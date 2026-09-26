using Yita.Interop;
using Yita.Models;

namespace Yita.Hooks;

internal sealed class NativeWindowDragTracker
{
    private const uint HitTestTimeoutMilliseconds = 25;
    private IntPtr _windowHandle;
    private IntPtr _hitTestWindowHandle;
    private NativeMethods.NativeRect _initialBounds;
    private bool _hasInitialBounds;
    private bool _startedInDragRegion;

    internal void Press(ScreenPoint point)
    {
        Cancel();
        var windowHandle = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint
        {
            X = point.X,
            Y = point.Y,
        });
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        _hitTestWindowHandle = windowHandle;
        _windowHandle = NativeMethods.GetAncestor(windowHandle, NativeMethods.GaRoot);
        if (_windowHandle == IntPtr.Zero)
        {
            _windowHandle = windowHandle;
        }

        _hasInitialBounds = NativeMethods.GetWindowRect(_windowHandle, out _initialBounds);
        _startedInDragRegion = TryGetHitTest(_hitTestWindowHandle, point, out var hitTest)
                               && IsDragHitTest(hitTest);
    }

    internal bool ReleaseShouldSuppressSelection()
    {
        var shouldSuppress = _startedInDragRegion;
        if (_hasInitialBounds
            && NativeMethods.GetWindowRect(_windowHandle, out var currentBounds)
            && HaveBoundsChanged(_initialBounds, currentBounds))
        {
            shouldSuppress = true;
        }

        Cancel();
        return shouldSuppress;
    }

    internal void Cancel()
    {
        _windowHandle = IntPtr.Zero;
        _hitTestWindowHandle = IntPtr.Zero;
        _initialBounds = default;
        _hasInitialBounds = false;
        _startedInDragRegion = false;
    }

    internal static bool HaveBoundsChanged(
        NativeMethods.NativeRect initialBounds,
        NativeMethods.NativeRect currentBounds)
    {
        return initialBounds.Left != currentBounds.Left
               || initialBounds.Top != currentBounds.Top
               || initialBounds.Right != currentBounds.Right
               || initialBounds.Bottom != currentBounds.Bottom;
    }

    internal static bool IsDragHitTest(int hitTest)
    {
        // A number of custom-framed apps (including WeChat) report large parts
        // of their client area as HTCAPTION. Treating that value as an automatic
        // suppression would discard legitimate text selections. Real title-bar
        // moves are still suppressed when the native window bounds change below.
        return hitTest is NativeMethods.HtSize
            or NativeMethods.HtHScroll
            or NativeMethods.HtVScroll
            || hitTest is >= NativeMethods.HtLeft and <= NativeMethods.HtBorder;
    }

    private static bool TryGetHitTest(IntPtr windowHandle, ScreenPoint point, out int hitTest)
    {
        var packedPoint = unchecked((int)((ushort)point.X | ((uint)(ushort)point.Y << 16)));
        var sent = NativeMethods.SendMessageTimeout(
            windowHandle,
            NativeMethods.WmNcHitTest,
            UIntPtr.Zero,
            new IntPtr(packedPoint),
            NativeMethods.SmtoAbortIfHung,
            HitTestTimeoutMilliseconds,
            out var result);
        hitTest = sent == IntPtr.Zero ? 0 : unchecked((int)result.ToUInt64());
        return sent != IntPtr.Zero;
    }
}
