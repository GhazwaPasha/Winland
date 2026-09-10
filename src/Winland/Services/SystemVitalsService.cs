using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using Winland.Interop;

namespace Winland.Services;

/// <summary>
/// Samples CPU/RAM/disk/GPU/network once a second for the Vitals and
/// Network tabs. Unlike the OS-signal services elsewhere in this app
/// (media, accent), none of these have a push/event API — they're all
/// read-on-demand counters — so this owns its own timer and raises one
/// <see cref="Changed"/> per tick, the same poll-and-diff shape
/// <see cref="PrivacyIndicatorService"/> uses for the same reason.
///
/// CPU/RAM/disk/GPU sample every tick regardless of what's on screen — Task
/// Manager and this app's own Vitals ring reveal both assume that data is
/// already fresh the instant the tab opens. Network is the one exception:
/// <see cref="SetNetworkSamplingEnabled"/> gates it off by default, so the
/// network-interface enumeration this needs only actually runs while the
/// Network tab is the one currently open (see NotchViewModel) rather than
/// continuously in the background regardless of whether anything's looking
/// at it.
/// </summary>
public sealed class SystemVitalsService : ISystemVitalsService, IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private const string GpuEngineCategory = "GPU Engine";
    private const string GpuUtilizationCounter = "Utilization Percentage";

    private readonly DispatcherTimer _timer;
    private readonly PerformanceCounter? _cpuCounter;
    private readonly string _systemDriveRoot;
    private readonly bool _gpuCounterAvailable;

    private DateTime _lastNetworkSampleAt;
    private long _lastBytesReceived;
    private long _lastBytesSent;

    // Off by default — "on demand" means no network sampling at all until
    // something actually asks for it (NotchViewModel enables this while the
    // Network tab is the open, expanded one). CPU/RAM/disk/GPU keep
    // sampling every tick regardless; this only gates the network half of
    // Refresh() below.
    private bool _networkSamplingEnabled;

    public SystemVitalsSnapshot Snapshot { get; private set; } = new(0, 0, 0, 0, 0, 0);

    public event EventHandler? Changed;

    public SystemVitalsService()
    {
        _systemDriveRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue(); // first call always returns 0 — primes the counter, discarded
        }
        catch
        {
            // Perf counter category unavailable (locked-down environment, etc.) — CPU stays 0.
            _cpuCounter = null;
        }

        // "GPU Engine" (Windows 10 1803+) has no single "_Total" instance the
        // way "Processor" does — just checking the category exists once here
        // avoids re-probing PerformanceCounterCategory.Exists on every tick.
        try
        {
            _gpuCounterAvailable = PerformanceCounterCategory.Exists(GpuEngineCategory);
        }
        catch
        {
            // Same "locked-down environment" possibility as the CPU counter above — GPU stays 0.
            _gpuCounterAvailable = false;
        }

        (_lastBytesReceived, _lastBytesSent) = ReadNetworkTotals();
        _lastNetworkSampleAt = DateTime.UtcNow;

        _timer = new DispatcherTimer { Interval = SampleInterval };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public void SetNetworkSamplingEnabled(bool enabled)
    {
        if (enabled == _networkSamplingEnabled)
        {
            return;
        }

        _networkSamplingEnabled = enabled;

        if (enabled)
        {
            // Reset the rate baseline to right now rather than whatever it
            // was left at the last time this was enabled — otherwise the
            // first tick after re-enabling would average bytes transferred
            // across the *entire* time it was off (could be minutes), not
            // the last second, producing one wildly wrong spike or trough
            // instead of a clean first reading.
            (_lastBytesReceived, _lastBytesSent) = ReadNetworkTotals();
            _lastNetworkSampleAt = DateTime.UtcNow;
        }
    }

    private void Refresh()
    {
        double cpu = 0, ram = 0, disk = 0, gpu = 0, down = 0, up = 0;

        try { cpu = _cpuCounter?.NextValue() ?? 0; }
        catch { /* counter can go stale if it's process-count-dependent; keep last-known-good via 0 */ }

        try { ram = ReadRamPercent(); }
        catch { }

        try { disk = ReadDiskPercent(); }
        catch { }

        try { gpu = ReadGpuPercent(); }
        catch { }

        try { (down, up) = _networkSamplingEnabled ? ReadNetworkRatesKBs() : (0, 0); }
        catch { }

        Snapshot = new SystemVitalsSnapshot(cpu, ram, disk, gpu, down, up);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static double ReadRamPercent()
    {
        var status = MEMORYSTATUSEX.Create();
        return NativeMethods.GlobalMemoryStatusEx(ref status) ? status.dwMemoryLoad : 0;
    }

    private double ReadDiskPercent()
    {
        var drive = new DriveInfo(_systemDriveRoot);
        if (!drive.IsReady || drive.TotalSize <= 0)
        {
            return 0;
        }

        var used = drive.TotalSize - drive.AvailableFreeSpace;
        return used * 100.0 / drive.TotalSize;
    }

    /// <summary>
    /// There's no single "GPU % used" counter the way "Processor\% Processor
    /// Time\_Total" exists for CPU — the "GPU Engine" category instead
    /// exposes one instance per process-per-engine (3D, Copy, VideoDecode,
    /// ...), created and destroyed as processes come and go, which is also
    /// why (unlike the CPU counter) these can't be built once in the
    /// constructor and reused. This sums "Utilization Percentage" across
    /// every 3D-engine instance, the same "engtype_3D" convention Task
    /// Manager's own GPU graph uses by default — a reasonable proxy for
    /// "how busy is the GPU" without pulling in a vendor SDK (NVML/ADL) just
    /// for one number on a status tab.
    /// </summary>
    private double ReadGpuPercent()
    {
        if (!_gpuCounterAvailable)
        {
            return 0;
        }

        var instanceNames = new PerformanceCounterCategory(GpuEngineCategory)
            .GetInstanceNames()
            .Where(name => name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase));

        double total = 0;
        foreach (var instanceName in instanceNames)
        {
            try
            {
                using var counter = new PerformanceCounter(GpuEngineCategory, GpuUtilizationCounter, instanceName, readOnly: true);
                total += counter.NextValue();
            }
            catch
            {
                // Instance can vanish between GetInstanceNames() and construction
                // (its process exited in between) — just skip it.
            }
        }

        return Math.Min(100, total);
    }

    private (double DownKBs, double UpKBs) ReadNetworkRatesKBs()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastNetworkSampleAt).TotalSeconds;
        var (received, sent) = ReadNetworkTotals();

        double downKBs = 0, upKBs = 0;
        if (elapsed > 0)
        {
            downKBs = Math.Max(0, received - _lastBytesReceived) / 1024.0 / elapsed;
            upKBs = Math.Max(0, sent - _lastBytesSent) / 1024.0 / elapsed;
        }

        _lastBytesReceived = received;
        _lastBytesSent = sent;
        _lastNetworkSampleAt = now;

        return (downKBs, upKBs);
    }

    private static (long Received, long Sent) ReadNetworkTotals()
    {
        long received = 0, sent = 0;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            var stats = nic.GetIPv4Statistics();
            received += stats.BytesReceived;
            sent += stats.BytesSent;
        }

        return (received, sent);
    }

    public void Dispose()
    {
        _timer.Stop();
        _cpuCounter?.Dispose();
    }
}
