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
/// dwLength must be set to this struct's own size before calling
/// GlobalMemoryStatusEx — the API uses it to version-check the buffer.
/// dwMemoryLoad arrives pre-computed by Windows as "approximate percentage
/// of physical memory in use", so the Vitals tab doesn't need to derive RAM%
/// itself from the total/available fields.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MEMORYSTATUSEX
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;

    public static MEMORYSTATUSEX Create() => new() { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
}

/// <summary>
/// Thin P/Invoke surface for the handful of things WPF has no managed API
/// for: detecting real exclusive fullscreen (the one thing that overrides
/// pin), sending the notch to the bottom of the z-order when it shouldn't
/// be on top of anything, and reading live physical memory usage for the
/// Vitals tab.
/// </summary>
internal static class NativeMethods
{
    private const string User32 = "user32.dll";
    private const string Kernel32 = "kernel32.dll";

    [DllImport(User32)]
    public static extern nint GetForegroundWindow();

    [DllImport(User32)]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport(User32)]
    public static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO lpmi);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport(User32, SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private static readonly nint HWND_BOTTOM = 1;

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
    /// Drops the notch to the literal bottom of the z-order — below every
    /// other top-level window, above only the desktop — without activating
    /// or moving/resizing it. Per Windows' own documented behavior for
    /// HWND_BOTTOM, this *also* clears the window's topmost status in the
    /// same call if it had one, so there's no separate "clear Topmost
    /// first" step needed.
    ///
    /// This replaced an earlier design that tried to slot the notch in
    /// directly behind whichever specific window currently had focus
    /// (PlaceBehind, since removed) — that needed continuously re-tracking
    /// a moving target and broke in several subtle ways (a click on the
    /// notch itself transiently changing the real foreground window; two
    /// different maximized windows never re-triggering re-anchoring since
    /// the classification between them didn't change). "Always at the
    /// absolute bottom" is a static placement that doesn't need
    /// re-anchoring at all: since nothing else contends for the very
    /// bottom, any window that becomes active is naturally inserted above
    /// it, with no further action needed on our part.
    /// </summary>
    public static void SendToBottom(nint hWnd)
    {
        SetWindowPos(hWnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Heuristic exclusive-fullscreen check: the foreground window's rect
    /// exactly covers its monitor's full bounds (not just the work area) —
    /// real exclusive-fullscreen apps (games, video players) satisfy this,
    /// an ordinary window (maximized or not) does not, since it still
    /// leaves room for the taskbar. This is the *only* foreground-window
    /// classification the pin behavior needs: whether something is merely
    /// maximized doesn't matter at all — see MainWindow's ApplyTopmostState
    /// for why "pinned XOR fullscreen" is the entire rule.
    /// </summary>
    public static bool IsForegroundFullscreen(nint fg, nint ignoreHwnd)
    {
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
