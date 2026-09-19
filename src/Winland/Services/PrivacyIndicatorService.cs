using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Winland.Services;

/// <summary>
/// Tells whether the microphone/camera are actively in use by *any* app
/// system-wide — not just this one — by reading the same registry store
/// Windows' own privacy dashboard reads.
///
/// Under HKCU\...\CapabilityAccessManager\ConsentStore\&lt;device&gt;, every
/// consumer that has ever requested the device gets its own leaf subkey:
/// packaged (Store) apps live directly under the device key, Win32 apps
/// live one level deeper under a "NonPackaged" subkey. Each leaf holds a
/// LastUsedTimeStop REG_QWORD that Windows sets to 0 while the device is
/// actively open, and to a real FILETIME once it's released — "in use" is
/// just "does any leaf currently read 0".
///
/// Instead of re-scanning that subtree every second, a dedicated background
/// thread parks on RegNotifyChangeKeyValue for the whole ConsentStore
/// subtree and only rescans when Windows actually writes something there
/// (i.e. when a device is opened or released). A slow safety rescan still
/// runs on a timeout in case a notification is ever missed, or the key
/// doesn't exist yet (nothing has requested a device on this machine) and
/// has to be retried. All of it — including the first scan at startup —
/// stays off the UI thread; <see cref="Changed"/> is raised from that
/// background thread and consumers marshal (NotchViewModel does).
/// </summary>
public sealed class PrivacyIndicatorService : IPrivacyIndicatorService, IDisposable
{
    private const string ConsentStoreKeyPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const string NonPackagedSubKeyName = "NonPackaged";
    private const string LastUsedTimeStopValueName = "LastUsedTimeStop";
    private static readonly TimeSpan SafetyRescanInterval = TimeSpan.FromSeconds(15);

    private const uint RegNotifyChangeName = 0x1;
    private const uint RegNotifyChangeLastSet = 0x4;
    // Without this, the registration is tied to the registering thread and
    // silently dies if that thread exits — harmless here (the watcher thread
    // lives as long as the service) but it's the safer contract.
    private const uint RegNotifyThreadAgnostic = 0x10000000;

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle hKey, [MarshalAs(UnmanagedType.Bool)] bool bWatchSubtree, uint dwNotifyFilter, SafeWaitHandle hEvent, [MarshalAs(UnmanagedType.Bool)] bool fAsynchronous);

    private readonly ManualResetEvent _stop = new(false);
    private readonly AutoResetEvent _registryChanged = new(false);
    private readonly Thread _thread;

    private volatile bool _isMicInUse;
    private volatile bool _isCameraInUse;

    public bool IsMicInUse => _isMicInUse;
    public bool IsCameraInUse => _isCameraInUse;

    public event EventHandler? Changed;

    public PrivacyIndicatorService()
    {
        _thread = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name = "Winland privacy watcher",
        };
        _thread.Start();
    }

    private void WatchLoop()
    {
        WaitHandle[] handles = { _stop, _registryChanged };
        RegistryKey? watchKey = null;

        try
        {
            var needsRegistration = true;

            while (true)
            {
                // The key stays open for the whole life of the thread: closing
                // a key with a pending notification signals the event, which
                // would wake the very next wait and spin this loop.
                if (watchKey is null)
                {
                    watchKey = TryOpenConsentStore();
                    needsRegistration = true;
                }

                // Registration is one-shot — re-arm it only after a change
                // actually fired (or on a fresh key). Done *before* scanning,
                // so a change landing between the scan and the wait isn't lost.
                if (watchKey is not null && needsRegistration)
                {
                    needsRegistration = false;
                    try
                    {
                        RegNotifyChangeKeyValue(
                            watchKey.Handle,
                            bWatchSubtree: true,
                            RegNotifyChangeName | RegNotifyChangeLastSet | RegNotifyThreadAgnostic,
                            _registryChanged.SafeWaitHandle,
                            fAsynchronous: true);
                    }
                    catch
                    {
                        // Registration failing just leaves the timed safety rescan below doing the work.
                    }
                }

                Refresh();

                // Signalled by a registry change (rescan promptly), by Dispose
                // (exit), or by the timeout (safety rescan / retry opening the key).
                var signalled = WaitHandle.WaitAny(handles, SafetyRescanInterval);
                if (signalled == 0)
                {
                    return;
                }

                if (signalled == 1)
                {
                    needsRegistration = true;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Disposed mid-wait — time to go.
        }
        finally
        {
            watchKey?.Dispose();
        }
    }

    private static RegistryKey? TryOpenConsentStore()
    {
        try
        {
            return Registry.CurrentUser.OpenSubKey(ConsentStoreKeyPath);
        }
        catch
        {
            return null;
        }
    }

    private void Refresh()
    {
        var micInUse = IsDeviceInUse("microphone");
        var camInUse = IsDeviceInUse("webcam");

        if (micInUse == _isMicInUse && camInUse == _isCameraInUse)
        {
            return;
        }

        _isMicInUse = micInUse;
        _isCameraInUse = camInUse;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsDeviceInUse(string deviceName)
    {
        try
        {
            using var deviceKey = Registry.CurrentUser.OpenSubKey($@"{ConsentStoreKeyPath}\{deviceName}");
            return deviceKey is not null && AnyLeafInUse(deviceKey);
        }
        catch
        {
            // Key missing (device never requested on this machine) or access
            // denied — either way, "not in use" is the safe default.
            return false;
        }
    }

    private static bool AnyLeafInUse(RegistryKey containerKey)
    {
        foreach (var childName in containerKey.GetSubKeyNames())
        {
            using var child = containerKey.OpenSubKey(childName);
            if (child is null)
            {
                continue;
            }

            // "NonPackaged" is itself a container of per-exe leaves, not a
            // leaf — recurse one level deeper for it, everything else at
            // this level is a real consumer identity.
            var isInUse = string.Equals(childName, NonPackagedSubKeyName, StringComparison.OrdinalIgnoreCase)
                ? AnyLeafInUse(child)
                : IsLeafInUse(child);

            if (isInUse)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLeafInUse(RegistryKey leafKey)
        => leafKey.GetValue(LastUsedTimeStopValueName) is long stop && stop == 0;

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromMilliseconds(500));
        _stop.Dispose();
        _registryChanged.Dispose();
    }
}
