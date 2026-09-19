using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Windows.Devices.Enumeration.Pnp;

namespace Winland.Services;

/// <summary>
/// Tracks the default audio render endpoint to tell whether headphones are
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
/// Connection state is event-driven: an IMMNotificationClient is registered
/// on the audio endpoint enumerator, so plugging in / pairing / switching
/// the default output refreshes within a fraction of a second, with no
/// polling at all. Battery % is the one piece Windows doesn't announce — it's
/// re-read on a slow timer (which also doubles as a backstop should an
/// endpoint notification ever be missed). Both used to be a 2-second poll
/// that enumerated every PnP device on the machine while a Bluetooth headset
/// was connected.
///
/// Everything here, including the first read at startup, runs on the thread
/// pool — <see cref="Changed"/> is raised from there and consumers marshal
/// to the UI thread themselves (NotchViewModel does).
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

    // Battery % has no change notification; this is also the safety-net
    // refresh for the (event-driven) connection state.
    private static readonly TimeSpan BatteryPollInterval = TimeSpan.FromSeconds(30);

    // Endpoint notifications arrive in bursts (a single connect fires
    // several) and on a COM callback thread that must not re-enter COM —
    // so they only nudge this short debounce timer, and the refresh itself
    // runs later on a pool thread.
    private static readonly TimeSpan EventDebounce = TimeSpan.FromMilliseconds(400);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Timer _debounceTimer;
    private readonly Timer _pollTimer;
    private readonly EndpointNotificationClient _notificationClient;
    private MMDeviceEnumerator? _enumerator;
    private volatile bool _disposed;
    private HeadphoneSnapshot _snapshot = new(false, false, null);

    public HeadphoneSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event EventHandler? Changed;

    public HeadphoneService()
    {
        _debounceTimer = new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _pollTimer = new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _notificationClient = new EndpointNotificationClient(() => _debounceTimer.Change(EventDebounce, Timeout.InfiniteTimeSpan));

        // COM enumerator creation, callback registration and the first
        // endpoint read all happen off the UI thread.
        _ = Task.Run(async () =>
        {
            try
            {
                _enumerator = new MMDeviceEnumerator();
                _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
            }
            catch
            {
                // No event source — the poll below still keeps state fresh, just slower.
            }

            await RefreshAsync();
            if (!_disposed)
            {
                _pollTimer.Change(BatteryPollInterval, BatteryPollInterval);
            }
        });
    }

    private async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _refreshLock.WaitAsync();
        try
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

            if (_disposed || snapshot == Snapshot)
            {
                return;
            }

            Volatile.Write(ref _snapshot, snapshot);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<HeadphoneSnapshot> ComputeSnapshotAsync()
    {
        MMDevice device;
        try
        {
            device = (_enumerator ?? throw new InvalidOperationException("Audio enumerator not ready")).GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
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
        _disposed = true;
        _debounceTimer.Dispose();
        _pollTimer.Dispose();

        try
        {
            _enumerator?.UnregisterEndpointNotificationCallback(_notificationClient);
        }
        catch
        {
            // Already gone — nothing to unregister.
        }

        _enumerator?.Dispose();
    }

    /// <summary>
    /// Only the notifications that can change "are headphones the current
    /// output" matter — property-value changes fire constantly (volume,
    /// peak meters, ...) and are ignored.
    /// </summary>
    private sealed class EndpointNotificationClient : IMMNotificationClient
    {
        private readonly Action _onRelevantChange;

        public EndpointNotificationClient(Action onRelevantChange) => _onRelevantChange = onRelevantChange;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render)
            {
                Nudge();
            }
        }

        public void OnDeviceAdded(string pwstrDeviceId) => Nudge();

        public void OnDeviceRemoved(string deviceId) => Nudge();

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Nudge();

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
        }

        private void Nudge()
        {
            try
            {
                _onRelevantChange();
            }
            catch (ObjectDisposedException)
            {
                // Service disposed while a notification was in flight.
            }
        }
    }
}
