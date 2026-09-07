using System;
using System.Windows.Threading;
using Winland.Interop;

namespace Winland.Services;

/// <summary>
/// Polls the foreground window once a second to detect real exclusive
/// fullscreen apps, so the notch window knows when it should hide itself
/// (when unpinned) versus stay put. Windows has no push notification for
/// "an app just went exclusive fullscreen" that a normal desktop app can
/// subscribe to, so polling is the pragmatic option here.
/// </summary>
public sealed class PinTopmostService : IDisposable
{
    private readonly nint _ownHwnd;
    private readonly DispatcherTimer _timer;
    private bool _lastFullscreen;

    public event EventHandler<bool>? FullscreenStateChanged;

    public PinTopmostService(nint ownHwnd, Dispatcher dispatcher)
    {
        _ownHwnd = ownHwnd;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    private void Poll()
    {
        var isFullscreen = NativeMethods.IsForegroundWindowFullscreen(_ownHwnd);
        if (isFullscreen == _lastFullscreen)
        {
            return;
        }

        _lastFullscreen = isFullscreen;
        FullscreenStateChanged?.Invoke(this, isFullscreen);
    }

    public void Dispose() => _timer.Stop();
}
