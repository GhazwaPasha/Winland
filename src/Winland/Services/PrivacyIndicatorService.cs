using System;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Winland.Services;

/// <summary>
/// Polls the same registry store Windows' own privacy dashboard reads to
/// tell whether the microphone/camera are actively in use by *any* app
/// system-wide — not just this one.
///
/// Under HKCU\...\CapabilityAccessManager\ConsentStore\&lt;device&gt;, every
/// consumer that has ever requested the device gets its own leaf subkey:
/// packaged (Store) apps live directly under the device key, Win32 apps
/// live one level deeper under a "NonPackaged" subkey. Each leaf holds a
/// LastUsedTimeStop REG_QWORD that Windows sets to 0 while the device is
/// actively open, and to a real FILETIME once it's released — "in use" is
/// just "does any leaf currently read 0".
///
/// There's no managed change-notification API for a registry *subtree*
/// scan like this, so this polls on a timer rather than subscribing to an
/// event — the same tradeoff BatteryService/AccentColorService make
/// wherever no real push signal exists.
/// </summary>
public sealed class PrivacyIndicatorService : IPrivacyIndicatorService, IDisposable
{
    private const string ConsentStoreKeyPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const string NonPackagedSubKeyName = "NonPackaged";
    private const string LastUsedTimeStopValueName = "LastUsedTimeStop";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _timer;

    public bool IsMicInUse { get; private set; }
    public bool IsCameraInUse { get; private set; }

    public event EventHandler? Changed;

    public PrivacyIndicatorService()
    {
        Refresh();

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    private void Refresh()
    {
        var micInUse = IsDeviceInUse("microphone");
        var camInUse = IsDeviceInUse("webcam");

        if (micInUse == IsMicInUse && camInUse == IsCameraInUse)
        {
            return;
        }

        IsMicInUse = micInUse;
        IsCameraInUse = camInUse;
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

    public void Dispose() => _timer.Stop();
}
