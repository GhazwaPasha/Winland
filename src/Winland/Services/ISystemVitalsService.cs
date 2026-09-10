using System;

namespace Winland.Services;

public sealed record SystemVitalsSnapshot(
    double CpuPercent,
    double RamPercent,
    double DiskPercent,
    double GpuPercent,
    double NetworkDownKBs,
    double NetworkUpKBs);

/// <summary>Live CPU/RAM/disk/GPU/network numbers for the Vitals and Network tabs.</summary>
public interface ISystemVitalsService
{
    SystemVitalsSnapshot Snapshot { get; }

    event EventHandler? Changed;

    /// <summary>
    /// Network throughput is sampled on demand, not continuously — enable
    /// this only while something is actually showing it (the Network tab),
    /// disable it the moment nothing is, per-tick CPU/RAM/disk/GPU sampling
    /// is unaffected either way. See SystemVitalsService's implementation
    /// for why enabling resets the rate baseline.
    /// </summary>
    void SetNetworkSamplingEnabled(bool enabled);
}
