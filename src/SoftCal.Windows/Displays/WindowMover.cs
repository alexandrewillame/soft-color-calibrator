using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SoftCal.Windows.Displays;

/// <summary>Where a window was: its rectangle (physical pixels) and whether it was maximized.</summary>
public readonly record struct WindowSpot(PixelBounds Bounds, bool Maximized);

/// <summary>
/// Moves a top-level window between monitors, in physical pixels; the window's own DPI scaling follows (WPF handles the
/// DPI change). Used to keep SoftCal's window off the screen the camera has to rest on.
/// </summary>
public static class WindowMover
{
    /// <summary>The monitor (HMONITOR) the window is mostly on.</summary>
    public static nint MonitorOf(nint hwnd) => PInvoke.MonitorFromWindow((HWND)hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

    /// <summary>Where the window is now; null when it cannot be read.</summary>
    public static WindowSpot? Remember(nint hwnd)
    {
        var handle = (HWND)hwnd;
        return PInvoke.GetWindowRect(handle, out var r)
            ? new WindowSpot(new PixelBounds(r.left, r.top, r.right - r.left, r.bottom - r.top), PInvoke.IsZoomed(handle))
            : null;
    }

    /// <summary>Centers the window in the monitor's work area. A maximized window is restored to its normal size
    /// first, so it does not cover the whole screen.</summary>
    public static unsafe void CenterOn(nint hwnd, DisplayMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var handle = (HWND)hwnd;
        if (PInvoke.IsZoomed(handle))
        {
            PInvoke.ShowWindow(handle, SHOW_WINDOW_CMD.SW_RESTORE);
        }

        var info = default(MONITORINFO);
        info.cbSize = (uint)sizeof(MONITORINFO);
        var b = monitor.Bounds;
        var (left, top, right, bottom) = PInvoke.GetMonitorInfo((HMONITOR)monitor.Handle, &info)
            ? (info.rcWork.left, info.rcWork.top, info.rcWork.right, info.rcWork.bottom)
            : (b.X, b.Y, b.Right, b.Bottom);
        if (!PInvoke.GetWindowRect(handle, out var r))
        {
            return;
        }

        var (width, height) = (r.right - r.left, r.bottom - r.top);
        var x = left + Math.Max(0, (right - left - width) / 2);
        var y = top + Math.Max(0, (bottom - top - height) / 2);
        PInvoke.SetWindowPos(handle, HWND.Null, x, y, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    /// <summary>Puts the window back where it was. A window that was maximized goes back to its screen at its normal
    /// size and is maximized there, so restoring it later gives its normal size back (not the screen's).</summary>
    public static void Restore(nint hwnd, WindowSpot spot)
    {
        var handle = (HWND)hwnd;
        if (PInvoke.IsZoomed(handle))
        {
            PInvoke.ShowWindow(handle, SHOW_WINDOW_CMD.SW_RESTORE);
        }

        var b = spot.Bounds;
        if (!spot.Maximized)
        {
            PInvoke.SetWindowPos(handle, HWND.Null, b.X, b.Y, b.Width, b.Height, SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
            return;
        }

        if (PInvoke.GetWindowRect(handle, out var r))
        {
            var (width, height) = (r.right - r.left, r.bottom - r.top);
            var x = b.X + Math.Max(0, (b.Width - width) / 2);
            var y = b.Y + Math.Max(0, (b.Height - height) / 2);
            PInvoke.SetWindowPos(handle, HWND.Null, x, y, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }

        PInvoke.ShowWindow(handle, SHOW_WINDOW_CMD.SW_MAXIMIZE);
    }
}
