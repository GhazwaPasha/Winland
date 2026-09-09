using System;

namespace Winland.Services;

public sealed record SystemVitalsSnapshot(
    double CpuPercent,
    double RamPercent,
    double DiskPercent,
    double GpuPercent,
    double NetworkDownKBs,
    double NetworkUpKBs);

/// <summary>Live CPU/RAM/disk/GPU/network numbers for the Vitals tab.</summary>
public interface ISystemVitalsService
{
    SystemVitalsSnapshot Snapshot { get; }

    event EventHandler? Changed;
}
