using System;
using System.IO;
using System.Windows.Threading;
using Winland.Interop;

namespace Winland.Services;

/// <summary>
/// Polls the foreground window to classify it as normal, maximized, or
/// real exclusive fullscreen — Windows has no push notification for either
/// transition that a normal desktop app can subscribe to, so polling is
/// the pragmatic option here. What the notch actually does with each state
/// (fullscreen always hides; maximized hides only when unpinned) lives in
/// MainWindow, not here — this service only reports what the foreground
/// window currently is.
///
/// Polls every 150ms (each tick is a handful of trivial P/Invoke calls, so
/// this costs nothing measurable) rather than once a second — hide/show is
/// a visible, immediate-feeling reaction to alt-tabbing or a window being
/// maximized, and a full second of lag there reads as broken, not just slow.
/// Runs at Normal priority rather than Background so it isn't starved
/// behind other UI work while the window itself is hidden and idle.
/// </summary>
public sealed class PinTopmostService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    private readonly nint _ownHwnd;
    private readonly DispatcherTimer _timer;
    private ForegroundWindowKind _lastState = ForegroundWindowKind.Normal;

    public event EventHandler<ForegroundWindowKind>? ForegroundWindowStateChanged;

    public PinTopmostService(nint ownHwnd, Dispatcher dispatcher)
    {
        _ownHwnd = ownHwnd;
        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
        {
            Interval = PollInterval,
        };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    private void Poll()
    {
        var state = NativeMethods.GetForegroundWindowKind(_ownHwnd);
        LogDiagnostics(state);

        if (state == _lastState)
        {
            return;
        }

        _lastState = state;
        ForegroundWindowStateChanged?.Invoke(this, state);
    }

    // TEMPORARY diagnostic logging while chasing the "still shows over a
    // maximized window even unpinned" report — same append-a-line-next-to-
    // the-exe technique AccentColorService already uses for exactly this
    // reason: a live mismatch between what we think is happening and what
    // Windows actually reports is a file read away instead of a guess.
    // Logs every poll (not just on change) so a flapping classification is
    // visible, not just the transitions we already react to. Remove once
    // the root cause is confirmed and fixed.
    private static void LogDiagnostics(ForegroundWindowKind state)
    {
        try
        {
            var fg = NativeMethods.GetForegroundWindow();
            var line = $"{DateTime.Now:HH:mm:ss.fff}  poll  state={state}  fg=0x{fg:X}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "pin-debug.log"), line);
        }
        catch
        {
            // Best-effort diagnostics only.
        }
    }

    public void Dispose() => _timer.Stop();
}
