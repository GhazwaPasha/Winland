using System.Runtime.InteropServices;

namespace Winland.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFO
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public int dwFlags;
}

/// <summary>
/// Thin P/Invoke surface used for the one thing WPF has no managed API for:
/// detecting when another app owns real exclusive fullscreen, so the pin
/// behavior knows whether to hide the notch.
/// </summary>
internal static class NativeMethods
{
    private const string User32 = "user32.dll";

    [DllImport(User32)]
    public static extern nint GetForegroundWindow();

    [DllImport(User32)]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport(User32)]
    public static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO lpmi);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport(User32, SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// Moves *and* resizes an HWND in one native call. WPF's own
    /// <c>Window.Width</c>/<c>Height</c>/<c>Left</c>/<c>Top</c> setters each
    /// issue their own independent, immediate <c>SetWindowPos</c> call under
    /// the hood — resize calls pass <c>SWP_NOMOVE</c> (so a width/height
    /// change resizes the window in place, anchored at whatever its *current*
    /// top-left happens to be) and move calls pass <c>SWP_NOSIZE</c>. Setting
    /// all four in sequence — as when the notch snaps down after collapsing —
    /// therefore passes through a real, distinct intermediate window state:
    /// already shrunk, but still anchored at the wider window's old (further
    /// left) edge, before a second call jumps it over to the recentred
    /// position. That intermediate state is exactly the single-frame "ghost"
    /// seen to the left of the collapsed pill. Doing both in one
    /// <c>SetWindowPos</c> call makes that intermediate state impossible: the
    /// window only ever exists at its old bounds or its final bounds.
    /// </summary>
    public static void MoveAndResizeWindow(nint hWnd, int x, int y, int width, int height)
    {
        SetWindowPos(hWnd, 0, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Heuristic exclusive-fullscreen check: the foreground window's client
    /// rect exactly covers its monitor's full bounds (not just the work
    /// area), and it isn't our own notch window.
    /// </summary>
    public static bool IsForegroundWindowFullscreen(nint ignoreHwnd)
    {
        var fg = GetForegroundWindow();
        if (fg == 0 || fg == ignoreHwnd)
        {
            return false;
        }

        if (!GetWindowRect(fg, out var windowRect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        if (monitor == 0)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            return false;
        }

        return windowRect.Left <= info.rcMonitor.Left
            && windowRect.Top <= info.rcMonitor.Top
            && windowRect.Right >= info.rcMonitor.Right
            && windowRect.Bottom >= info.rcMonitor.Bottom;
    }
}
