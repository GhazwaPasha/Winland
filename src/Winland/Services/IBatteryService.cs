using System;

namespace Winland.Services;

public interface IBatteryService
{
    int CurrentPercent { get; }
    event EventHandler? BatteryChanged;
}
