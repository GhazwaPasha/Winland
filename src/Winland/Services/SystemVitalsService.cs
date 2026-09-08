using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using Winland.Interop;

namespace Winland.Services;

/// <summary>
/// Samples CPU/RAM/disk/network once a second for the Vitals tab. Unlike
/// the OS-signal services elsewhere in this app (battery, media, accent),
/// none of these have a push/event API — they're all read-on-demand
/// counters — so this owns its own timer and raises one <see cref="Changed"/>
/// per tick, the same poll-and-diff shape <see cref="PrivacyIndicatorService"/>
/// uses for the same reason.
/// </summary>
public sealed class SystemVitalsService : ISystemVitalsService, IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _timer;
    private readonly PerformanceCounter? _cpuCounter;
    private readonly string _systemDriveRoot;

    private DateTime _lastNetworkSampleAt;
    private long _lastBytesReceived;
    private long _lastBytesSent;

    public SystemVitalsSnapshot Snapshot { get; private set; } = new(0, 0, 0, 0, 0);

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

        (_lastBytesReceived, _lastBytesSent) = ReadNetworkTotals();
        _lastNetworkSampleAt = DateTime.UtcNow;

        _timer = new DispatcherTimer { Interval = SampleInterval };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    private void Refresh()
    {
        double cpu = 0, ram = 0, disk = 0, down = 0, up = 0;

        try { cpu = _cpuCounter?.NextValue() ?? 0; }
        catch { /* counter can go stale if it's process-count-dependent; keep last-known-good via 0 */ }

        try { ram = ReadRamPercent(); }
        catch { }

        try { disk = ReadDiskPercent(); }
        catch { }

        try { (down, up) = ReadNetworkRatesKBs(); }
        catch { }

        Snapshot = new SystemVitalsSnapshot(cpu, ram, disk, down, up);
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
