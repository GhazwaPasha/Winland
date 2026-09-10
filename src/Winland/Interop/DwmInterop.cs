using System;
using System.Runtime.InteropServices;

namespace Winland.Interop;

/// <summary>
/// The DWM window-attribute surface SettingsWindow needs for a native Win11
/// Mica backdrop with a dark title bar — split out from NativeMethods.cs the
/// same way WlanInterop is: a distinct native concern (desktop window
/// manager attributes) gets its own file rather than growing that class's
/// existing "fullscreen/z-order/vitals" scope.
/// </summary>
internal static class DwmInterop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS pMarInset);

    // DWMWA_* values from dwmapi.h — not exposed anywhere in .NET's own BCL.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    // DWM_SYSTEMBACKDROP_TYPE enum. MainWindow (2) is the plain "Mica" look
    // (as opposed to TabbedWindow/"Mica Alt", 4, or Transient/"Acrylic", 3) —
    // the same backdrop the Settings app's own window uses.
    private const int DWMSBT_MAINWINDOW = 2;

    // Mica (and DWMWA_SYSTEMBACKDROP_TYPE itself) only exist from Windows 11
    // 21H2 onward; DWMWA_USE_IMMERSIVE_DARK_MODE works back to Windows 10
    // 20H1 but is harmless to call unconditionally on 11 too.
    private const int Windows11Build = 22000;

    /// <summary>
    /// Applies a dark title bar everywhere it's supported, and Mica on
    /// Windows 11. Setting DWMWA_SYSTEMBACKDROP_TYPE alone isn't sufficient
    /// for a WPF window — a non-layered HWND (AllowsTransparency="False",
    /// what SettingsWindow uses) still has WPF paint an *opaque* background
    /// into its own client-area render target regardless of what
    /// Window.Background is set to in XAML, which paints straight over
    /// whatever DWM composites behind it. DwmExtendFrameIntoClientArea with
    /// a -1/-1/-1/-1 "sheet of glass" margin is what tells DWM this entire
    /// window is a backdrop surface; SettingsWindow.xaml.cs pairs this call
    /// with the other missing half, forcing its own HwndSource's
    /// CompositionTarget.BackgroundColor to Transparent so WPF actually
    /// stops opaquely painting over that surface. All three calls are
    /// fire-and-forget: a failure (older Windows build, attribute not
    /// recognized) just leaves the window with its default chrome instead
    /// of throwing — a settings window that opens without Mica is a fine
    /// fallback, one that fails to open is not.
    /// </summary>
    public static void ApplyMicaBackdrop(nint hwnd)
    {
        try
        {
            var darkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            if (Environment.OSVersion.Version.Build >= Windows11Build)
            {
                var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);

                var backdrop = DWMSBT_MAINWINDOW;
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            }
        }
        catch
        {
            // Best-effort visual flourish — never worth failing the settings window over.
        }
    }
}
