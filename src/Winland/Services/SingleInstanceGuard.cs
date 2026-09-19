using System;
using System.Threading;

namespace Winland.Services;

/// <summary>
/// Keeps Winland to one running copy per Windows session. The first launch
/// takes a named mutex and listens on a named event; any later launch fails
/// to take the mutex, pings that event (so the running copy can un-hide
/// itself if it's sitting in the tray) and reports "not primary" so the
/// caller can exit before building anything.
///
/// "Local\" scopes the names to the current logon session, so two users on
/// the same PC (fast user switching) each still get their own copy. The
/// event is created by both sides — whoever gets there first makes it — so a
/// second launch that races the first's startup still leaves a signal
/// behind for the listener to pick up once it starts waiting.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\PashaFoundry.Wisland.SingleInstance";
    private const string ActivateEventName = @"Local\PashaFoundry.Wisland.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly bool _isPrimary;
    private volatile bool _disposed;

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out _isPrimary);
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
    }

    /// <summary>True for the first launch; false if another copy already owns the mutex.</summary>
    public bool IsPrimary => _isPrimary;

    /// <summary>Tells the already-running copy that someone tried to launch a second one.</summary>
    public void SignalPrimary() => _activateEvent.Set();

    /// <summary>
    /// Runs <paramref name="onSecondLaunch"/> (on a background thread — marshal
    /// to the UI thread yourself) every time a later launch pings this copy.
    /// Primary-only.
    /// </summary>
    public void ListenForSecondLaunch(Action onSecondLaunch)
    {
        var thread = new Thread(() =>
        {
            while (!_disposed)
            {
                try
                {
                    _activateEvent.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (_disposed)
                {
                    return;
                }

                onSecondLaunch();
            }
        })
        {
            IsBackground = true,
            Name = "Winland single-instance listener",
        };
        thread.Start();
    }

    public void Dispose()
    {
        _disposed = true;

        if (_isPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread — the OS releases it at process exit regardless.
            }

            _activateEvent.Set(); // Wake the listener so it sees _disposed and returns.
        }

        _mutex.Dispose();
        _activateEvent.Dispose();
    }
}
