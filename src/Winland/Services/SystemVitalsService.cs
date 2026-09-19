using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using Winland.Interop;

namespace Winland.Services;

/// <summary>
/// Samples CPU/RAM/disk/GPU/network for the Vitals and Network tabs. None of
/// these have a push/event API — they're all read-on-demand counters — so
/// this owns its own timer and raises one <see cref="Changed"/> per tick.
///
/// Everything is on demand: nothing is sampled (and nothing is even
/// initialized — no perf counters, no NIC enumeration) until
/// <see cref="SetVitalsSamplingEnabled"/> / <see cref="SetNetworkSamplingEnabled"/>
/// is switched on for a tab that's actually on screen, and the timer goes
/// fully idle again the moment both are off. Sampling used to run every
/// second forever regardless of what was visible, and its GPU read alone
/// (a fresh PerformanceCounter per GPU engine instance, on the UI thread)
/// cost ~350ms per tick.
///
/// Sampling runs on the thread pool and <see cref="Changed"/> is raised from
/// there — consumers marshal to the UI thread themselves (NotchViewModel does).
/// The timer is re-armed after each sample instead of using a periodic
/// period, so a slow sample (the very first GPU category read is slow) can
/// never pile up overlapping ticks.
/// </summary>
public sealed class SystemVitalsService : ISystemVitalsService, IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private const string GpuEngineCategory = "GPU Engine";
    private const string GpuUtilizationCounter = "Utilization Percentage";

    private readonly object _gate = new();
    private readonly Timer _timer;
    private readonly string _systemDriveRoot;

    private SystemVitalsSnapshot _snapshot = new(0, 0, 0, 0, 0, 0);
    private bool _vitalsEnabled;
    private bool _networkEnabled;
    private bool _disposed;

    // CPU: cumulative system times at the previous sample. _haveCpuBaseline
    // is false right after sampling is (re-)enabled — the first read then
    // only records a fresh baseline instead of averaging over however long
    // sampling was off.
    private bool _haveCpuBaseline;
    private ulong _lastIdle, _lastKernel, _lastUser;

    // GPU: previous raw sample per "engtype_3D" instance. Utilization
    // Percentage is a rate counter — its value is only defined between two
    // samples of the *same* instance, so these must persist across ticks
    // (a counter built fresh each tick always reads 0 on its first
    // NextValue, which is why the GPU ring never showed real data before).
    //
    // The GPU read runs on its own pool work item, never inline with the rest
    // of the sample: the very first read of the category costs ~5s in .NET
    // (cold perf-counter initialization), and inline that would hold back
    // RAM/disk/CPU for as long. Sample() just publishes the latest finished
    // GPU value (one tick behind, which nobody can see on a ring) and kicks
    // off the next read unless one is already running.
    private Dictionary<string, CounterSample> _gpuPrevious = new();
    private bool _gpuAvailable = true;
    private double _gpuLatest;
    private int _gpuReadInFlight;
    private volatile bool _gpuResetRequested;

    private DateTime _lastNetworkSampleAt;
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private bool _haveNetworkBaseline;

    public SystemVitalsSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event EventHandler? Changed;

    public SystemVitalsService()
    {
        _systemDriveRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        _timer = new Timer(_ => SampleAndRearm(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void SetVitalsSamplingEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (enabled == _vitalsEnabled || _disposed)
            {
                return;
            }

            _vitalsEnabled = enabled;
            if (enabled)
            {
                _haveCpuBaseline = false;
                _gpuResetRequested = true; // consumed by the GPU worker, which owns _gpuPrevious
            }
        }

        if (enabled)
        {
            // Seeds the CPU/GPU baselines and fills RAM/disk right away, so
            // the tab isn't empty for a whole interval; CPU/GPU show their
            // last known values until the first real tick a second later.
            ThreadPool.QueueUserWorkItem(_ => SampleAndRearm());
        }
        else
        {
            Rearm();
        }
    }

    public void SetNetworkSamplingEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (enabled == _networkEnabled || _disposed)
            {
                return;
            }

            _networkEnabled = enabled;

            // Re-baseline on enable rather than reuse whatever was left the
            // last time this was on — otherwise the first tick would average
            // bytes across the *entire* time it was off (minutes, possibly),
            // producing one wildly wrong reading instead of a clean one.
            if (enabled)
            {
                _haveNetworkBaseline = false;
            }
        }

        if (enabled)
        {
            ThreadPool.QueueUserWorkItem(_ => SampleAndRearm());
        }
        else
        {
            Rearm();
        }
    }

    private void SampleAndRearm()
    {
        SystemVitalsSnapshot? changed = null;

        lock (_gate)
        {
            if (_disposed || (!_vitalsEnabled && !_networkEnabled))
            {
                return;
            }

            changed = Sample();
        }

        Rearm();

        if (changed is not null)
        {
            Volatile.Write(ref _snapshot, changed);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Rearm()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var active = _vitalsEnabled || _networkEnabled;
            _timer.Change(active ? SampleInterval : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private SystemVitalsSnapshot Sample()
    {
        var current = Snapshot;
        double cpu = current.CpuPercent, ram = current.RamPercent, disk = current.DiskPercent, gpu = current.GpuPercent;
        double down = 0, up = 0;

        if (_vitalsEnabled)
        {
            try { cpu = ReadCpuPercent() ?? cpu; }
            catch { /* keep last-known-good */ }

            try { ram = ReadRamPercent(); }
            catch { }

            try { disk = ReadDiskPercent(); }
            catch { }

            QueueGpuRead();
            gpu = Volatile.Read(ref _gpuLatest);
        }

        if (_networkEnabled)
        {
            try { (down, up) = ReadNetworkRatesKBs(); }
            catch { }
        }

        return new SystemVitalsSnapshot(cpu, ram, disk, gpu, down, up);
    }

    /// <summary>Null on the first call after enabling (baseline only) or if the read fails.</summary>
    private double? ReadCpuPercent()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        var hadBaseline = _haveCpuBaseline;
        var idleDelta = idle - _lastIdle;
        var totalDelta = (kernel - _lastKernel) + (user - _lastUser); // kernel already includes idle

        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;
        _haveCpuBaseline = true;

        if (!hadBaseline || totalDelta == 0)
        {
            return null;
        }

        return Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100);
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

    private void QueueGpuRead()
    {
        if (Interlocked.CompareExchange(ref _gpuReadInFlight, 1, 0) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (_gpuResetRequested)
                {
                    _gpuResetRequested = false;
                    _gpuPrevious = new Dictionary<string, CounterSample>();
                }

                var value = ReadGpuPercent();
                if (value is not null)
                {
                    Volatile.Write(ref _gpuLatest, value.Value);
                }
            }
            catch
            {
                // Keep the last known GPU value.
            }
            finally
            {
                Volatile.Write(ref _gpuReadInFlight, 0);
            }
        });
    }

    /// <summary>
    /// There's no single "GPU % used" counter the way CPU has one — the "GPU
    /// Engine" category exposes one instance per process-per-engine (3D,
    /// Copy, VideoDecode, ...), created and destroyed as processes come and
    /// go. This sums "Utilization Percentage" across every 3D-engine
    /// instance, the same "engtype_3D" convention Task Manager's own GPU
    /// graph uses by default — a reasonable proxy for "how busy is the GPU"
    /// without a vendor SDK (NVML/ADL) for one number on a status tab.
    ///
    /// Runs only on the GPU work item (see <see cref="QueueGpuRead"/>).
    /// One <see cref="PerformanceCounterCategory.ReadCategory"/> per tick
    /// snapshots every instance at once (~2ms), and the value is computed
    /// between this tick's raw sample and the previous tick's for the same
    /// instance. Null on the first call (baseline only) or if the category
    /// isn't available on this machine.
    /// </summary>
    private double? ReadGpuPercent()
    {
        if (!_gpuAvailable)
        {
            return null;
        }

        System.Diagnostics.InstanceDataCollection samples;
        try
        {
            samples = new PerformanceCounterCategory(GpuEngineCategory).ReadCategory()[GpuUtilizationCounter];
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or NullReferenceException)
        {
            // Category missing (pre-1803 Windows) or perf data locked down — GPU stays at 0.
            _gpuAvailable = false;
            return null;
        }

        if (samples is null)
        {
            _gpuAvailable = false;
            return null;
        }

        var previous = _gpuPrevious;
        var next = new Dictionary<string, CounterSample>(previous.Count);
        var hadBaseline = previous.Count > 0;
        double total = 0;

        foreach (System.Collections.DictionaryEntry entry in samples)
        {
            var name = (string)entry.Key;
            if (!name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sample = ((InstanceData)entry.Value!).Sample;
            next[name] = sample;

            if (previous.TryGetValue(name, out var before))
            {
                try
                {
                    total += CounterSample.Calculate(before, sample);
                }
                catch
                {
                    // Instance reused by a new process between ticks — skip this one reading.
                }
            }
        }

        _gpuPrevious = next; // also drops instances whose process has exited
        return hadBaseline ? Math.Min(100, total) : null;
    }

    private (double DownKBs, double UpKBs) ReadNetworkRatesKBs()
    {
        var now = DateTime.UtcNow;
        var (received, sent) = ReadNetworkTotals();

        double downKBs = 0, upKBs = 0;
        var elapsed = (now - _lastNetworkSampleAt).TotalSeconds;
        if (_haveNetworkBaseline && elapsed > 0)
        {
            downKBs = Math.Max(0, received - _lastBytesReceived) / 1024.0 / elapsed;
            upKBs = Math.Max(0, sent - _lastBytesSent) / 1024.0 / elapsed;
        }

        _lastBytesReceived = received;
        _lastBytesSent = sent;
        _lastNetworkSampleAt = now;
        _haveNetworkBaseline = true;

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
        lock (_gate)
        {
            _disposed = true;
        }

        _timer.Dispose();
    }
}
