using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Windows.Devices.Enumeration.Pnp;

namespace Winland.Services;

/// <summary>
/// Polls the default audio render endpoint to tell whether headphones are
/// the current output, and — for a Bluetooth pair — their battery %.
///
/// Two things worth recording since neither was obvious going in:
///
/// <list type="bullet">
/// <item>PKEY_AudioEndpoint_FormFactor alone can't tell wired from
/// wireless: a Bluetooth A2DP endpoint reports FormFactor "Headphones"
/// (3) — identically to a real analog jack — confirmed by testing against
/// an actual paired Bluetooth headset. What *does* reliably distinguish
/// them is DEVPKEY_Device_EnumeratorName ({a45c254e-df1c-4efd-8020-
/// 67d146a850e0},24) — a standard, documented Windows device property
/// (unrelated to NAudio's own named PropertyKeys) — which reads "HDAUDIO"
/// for the onboard jack and "BTHENUM"/"BTHHFENUM" for a Bluetooth
/// accessory's A2DP/hands-free profile, also confirmed against real
/// hardware.</item>
/// <item>The battery-percent property below (see
/// <see cref="BatteryPropertyKey"/>) is genuinely undocumented — see the
/// implementation-plan writeup — confirmed working here, but with no
/// compatibility guarantee from Microsoft.</item>
/// </list>
///
/// Like <see cref="PrivacyIndicatorService"/> and <see cref="SystemVitalsService"/>,
/// this polls on its own timer rather than subscribing to a change event —
/// consistent with how this app handles every signal that has no clean
/// push API, and it sidesteps needing to register/unregister a native
/// endpoint-notification callback for a once-every-couple-seconds signal
/// that doesn't need to be instant.
/// </summary>
public sealed class HeadphoneService : IHeadphoneService, IDisposable
{
    private static readonly PropertyKey EnumeratorNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    // NAudio surfaces PKEY_AudioEndpoint_FormFactor as a raw int rather than
    // a named enum — these are the well-known EndpointFormFactor values
    // (mmdeviceapi.h), confirmed against real hardware: 3 for a wired
    // headphone jack, and — surprisingly — also 3 for a Bluetooth A2DP
    // endpoint (hence needing EnumeratorNameKey above to actually tell them
    // apart), 5 for a Bluetooth hands-free "Headset" endpoint.
    private const int FormFactorHeadphones = 3;
    private const int FormFactorHeadset = 5;

    private const string BatteryPropertyKey = "{104ea319-6ee2-4701-bd47-8ddbf425bbe5} 2";
    private const string NamePropertyKey = "System.ItemNameDisplay";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly DispatcherTimer _timer;

    public HeadphoneSnapshot Snapshot { get; private set; } = new(false, false, null);

    public event EventHandler? Changed;

    public HeadphoneService()
    {
        _ = RefreshAsync();

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
    }

    private async Task RefreshAsync()
    {
        HeadphoneSnapshot snapshot;
        try
        {
            snapshot = await ComputeSnapshotAsync();
        }
        catch
        {
            snapshot = new HeadphoneSnapshot(false, false, null);
        }

        if (snapshot == Snapshot)
        {
            return;
        }

        Snapshot = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<HeadphoneSnapshot> ComputeSnapshotAsync()
    {
        MMDevice device;
        try
        {
            device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            // No default render endpoint at all (rare — e.g. mid device change).
            return new HeadphoneSnapshot(false, false, null);
        }

        using (device)
        {
            var formFactor = ReadFormFactor(device);
            if (formFactor != FormFactorHeadphones && formFactor != FormFactorHeadset)
            {
                return new HeadphoneSnapshot(false, false, null);
            }

            var isWireless = ReadEnumeratorName(device).StartsWith("BTH", StringComparison.OrdinalIgnoreCase);
            if (!isWireless)
            {
                return new HeadphoneSnapshot(true, false, null);
            }

            var battery = await TryReadBatteryPercentAsync(device.DeviceFriendlyName);
            return new HeadphoneSnapshot(true, true, battery);
        }
    }

    private const int FormFactorUnknown = -1;

    private static int ReadFormFactor(MMDevice device)
    {
        try
        {
            var raw = device.Properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor].Value;
            return raw is null ? FormFactorUnknown : Convert.ToInt32(raw);
        }
        catch
        {
            return FormFactorUnknown;
        }
    }

    private static string ReadEnumeratorName(MMDevice device)
    {
        try
        {
            return device.Properties[EnumeratorNameKey].Value as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The audio endpoint and the underlying Bluetooth accessory's battery
    /// entry don't share an ID space, so this matches by name — the audio
    /// endpoint's device name (e.g. "Galaxy Buds Live (1731)") is a
    /// substring of the PnP battery entry's name (e.g. "Galaxy Buds Live
    /// (1731) Hands-Free AG") for the same physical accessory, confirmed
    /// during implementation.
    /// </summary>
    private static async Task<int?> TryReadBatteryPercentAsync(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        try
        {
            var pnpDevices = await PnpObject.FindAllAsync(PnpObjectType.Device, new[] { BatteryPropertyKey, NamePropertyKey });
            foreach (var pnpDevice in pnpDevices)
            {
                if (!pnpDevice.Properties.TryGetValue(BatteryPropertyKey, out var batteryValue) || batteryValue is not byte battery)
                {
                    continue;
                }

                pnpDevice.Properties.TryGetValue(NamePropertyKey, out var nameValue);
                if (nameValue is string pnpName && pnpName.Contains(deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return battery;
                }
            }
        }
        catch
        {
            // Best effort — connection state (already resolved) still stands without a percentage.
        }

        return null;
    }

    public void Dispose()
    {
        _timer.Stop();
        _enumerator.Dispose();
    }
}
