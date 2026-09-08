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
/// What the foreground window currently is, for the pin behavior:
/// <see cref="Fullscreen"/> (real exclusive fullscreen — a game, a video
/// player) always lets the notch get covered, regardless of pin state,
/// while <see cref="Maximized"/> is the one the pin actually governs —
/// pinned stays on top of it, unpinned lets it cover the notch.
/// <see cref="Normal"/> never lets anything cover the notch. The notch
/// itself is never hidden for any of this — see MainWindow's
/// ApplyTopmostState — it just drops out of the topmost z-order band so an
/// ordinary window naturally paints over it.
/// </summary>
public enum ForegroundWindowKind
{
    Normal,
    Maximized,
    Fullscreen,
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
/// for: classifying the foreground window as normal/maximized/fullscreen
/// (MainWindow's ApplyTopmostState reacts to that via WPF's own Topmost
/// property, not a native call — see its doc comment for why), and reading
/// live physical memory usage for the Vitals tab.
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

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsZoomed(nint hWnd);

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
    /// Inserts <paramref name="hWnd"/> immediately behind <paramref
    /// name="targetHWnd"/> in the z-order, without activating or
    /// moving/resizing either window. This is deliberately NOT the same
    /// thing as <c>Window.Topmost = false</c> (which uses the special
    /// HWND_NOTOPMOST value): per Windows' own documented behavior,
    /// HWND_NOTOPMOST places a window at the *front* of the entire
    /// non-topmost band, not wherever it would naturally end up relative to
    /// whatever's currently focused — so merely dropping Topmost never
    /// actually puts the notch behind a specific maximized/fullscreen
    /// window, it just stops it from being above the topmost band, while
    /// still rendering above that window (confirmed live: the classification
    /// and the Topmost=false call were both firing correctly and exactly
    /// once, yet the notch stayed visibly on top the whole time). Passing a
    /// real window handle here instead inserts directly below that specific
    /// window, which is the only way to actually get covered by it.
    /// </summary>
    public static void PlaceBehind(nint hWnd, nint targetHWnd)
    {
        SetWindowPos(hWnd, targetHWnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Classifies the foreground window (ignoring our own notch window):
    /// <see cref="ForegroundWindowKind.Fullscreen"/> when its rect exactly
    /// covers the monitor's full bounds (not just the work area) — the
    /// heuristic real exclusive-fullscreen apps (games, video players)
    /// satisfy and an ordinary maximized window does not, since a maximized
    /// window still leaves room for the taskbar.
    ///
    /// Otherwise <see cref="ForegroundWindowKind.Maximized"/> when either
    /// <see cref="IsZoomed"/> (the real WS_MAXIMIZE style bit) is set, *or*
    /// the window's rect covers the monitor's work area on its own —
    /// plenty of modern apps with a custom title bar (Windows Terminal,
    /// VS Code, Chromium-based browsers, etc.) implement "maximize" by
    /// resizing themselves to the work area by hand rather than calling
    /// the real OS maximize, so IsZoomed alone misses them and the notch
    /// would wrongly stay topmost over them regardless of pin. Otherwise
    /// <see cref="ForegroundWindowKind.Normal"/>.
    /// </summary>
    public static ForegroundWindowKind GetForegroundWindowKind(nint ignoreHwnd)
    {
        var fg = GetForegroundWindow();
        if (fg == 0 || fg == ignoreHwnd)
        {
            return ForegroundWindowKind.Normal;
        }

        if (!GetWindowRect(fg, out var windowRect))
        {
            return IsZoomed(fg) ? ForegroundWindowKind.Maximized : ForegroundWindowKind.Normal;
        }

        var monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        if (monitor == 0)
        {
            return IsZoomed(fg) ? ForegroundWindowKind.Maximized : ForegroundWindowKind.Normal;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            return IsZoomed(fg) ? ForegroundWindowKind.Maximized : ForegroundWindowKind.Normal;
        }

        if (windowRect.Left <= info.rcMonitor.Left
            && windowRect.Top <= info.rcMonitor.Top
            && windowRect.Right >= info.rcMonitor.Right
            && windowRect.Bottom >= info.rcMonitor.Bottom)
        {
            return ForegroundWindowKind.Fullscreen;
        }

        var coversWorkArea = windowRect.Left <= info.rcWork.Left
            && windowRect.Top <= info.rcWork.Top
            && windowRect.Right >= info.rcWork.Right
            && windowRect.Bottom >= info.rcWork.Bottom;

        return IsZoomed(fg) || coversWorkArea ? ForegroundWindowKind.Maximized : ForegroundWindowKind.Normal;
    }
}
