using System;
using System.Windows.Threading;
using Winland.Interop;

namespace Winland.Services;

/// <summary>
/// Polls whether the foreground window is real exclusive fullscreen —
/// Windows has no push notification for that transition a normal desktop
/// app can subscribe to, so polling is the pragmatic option here. That's
/// the only thing this reports: combining it with the pin state to decide
/// what the notch's z-order should actually be lives in MainWindow's
/// ApplyTopmostState.
///
/// Polls every 150ms (each tick is a handful of trivial P/Invoke calls, so
/// this costs nothing measurable) rather than once a second — reacting to
/// a fullscreen app launching is a visible, immediate-feeling thing, and a
/// full second of lag there reads as broken, not just slow. Runs at Normal
/// priority rather than Background so it isn't starved behind other UI
/// work while the window itself is sitting at the bottom of the z-order
/// and otherwise idle.
/// </summary>
public sealed class PinTopmostService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    private readonly nint _ownHwnd;
    private readonly DispatcherTimer _timer;
    private bool _lastIsFullscreen;

    public event EventHandler<bool>? FullscreenStateChanged;

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
        var fg = NativeMethods.GetForegroundWindow();
        var isFullscreen = NativeMethods.IsForegroundFullscreen(fg, _ownHwnd);

        if (isFullscreen == _lastIsFullscreen)
        {
            return;
        }

        _lastIsFullscreen = isFullscreen;
        FullscreenStateChanged?.Invoke(this, isFullscreen);
    }

    public void Dispose() => _timer.Stop();
}
