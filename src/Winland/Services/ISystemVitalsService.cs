using System;

namespace Winland.Services;

public sealed record SystemVitalsSnapshot(
    double CpuPercent,
    double RamPercent,
    double DiskPercent,
    double NetworkDownKBs,
    double NetworkUpKBs);

/// <summary>Live CPU/RAM/disk/network numbers for the Vitals tab.</summary>
public interface ISystemVitalsService
{
    SystemVitalsSnapshot Snapshot { get; }

    event EventHandler? Changed;
}
