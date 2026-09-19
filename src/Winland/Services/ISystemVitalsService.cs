using System;

namespace Winland.Services;

public sealed record SystemVitalsSnapshot(
    double CpuPercent,
    double RamPercent,
    double DiskPercent,
    double GpuPercent,
    double NetworkDownKBs,
    double NetworkUpKBs);

/// <summary>
/// Live CPU/RAM/disk/GPU/network numbers for the Vitals and Network tabs.
/// Both halves are sampled on demand — see the two Set*SamplingEnabled
/// methods — and <see cref="Changed"/> is raised from a background thread.
/// </summary>
public interface ISystemVitalsService
{
    SystemVitalsSnapshot Snapshot { get; }

    event EventHandler? Changed;

    /// <summary>
    /// CPU/RAM/disk/GPU are only sampled while something is actually
    /// showing them (the Vitals tab) — enable this then, disable it the
    /// moment nothing is. Enabling re-baselines the rate counters, so CPU
    /// and GPU keep their last value for up to one interval before the
    /// first fresh reading; RAM and disk refresh immediately.
    /// </summary>
    void SetVitalsSamplingEnabled(bool enabled);

    /// <summary>
    /// Network throughput is sampled on demand too — enable this only while
    /// the Network tab is showing. Enabling resets the rate baseline (see
    /// SystemVitalsService).
    /// </summary>
    void SetNetworkSamplingEnabled(bool enabled);
}
