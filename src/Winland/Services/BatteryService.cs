using System;
using Windows.Devices.Power;

namespace Winland.Services;

/// <summary>Read-only battery status for the pill/header glyph — not a setting, so kept real.</summary>
public sealed class BatteryService : IBatteryService, IDisposable
{
    private readonly Battery _aggregateBattery;

    public int CurrentPercent { get; private set; } = 100;

    public event EventHandler? BatteryChanged;

    public BatteryService()
    {
        _aggregateBattery = Battery.AggregateBattery;
        _aggregateBattery.ReportUpdated += OnReportUpdated;
        UpdateFromReport();
    }

    private void OnReportUpdated(Battery sender, object args) => UpdateFromReport();

    private void UpdateFromReport()
    {
        try
        {
            var report = _aggregateBattery.GetReport();
            var full = report.FullChargeCapacityInMilliwattHours;
            var remaining = report.RemainingCapacityInMilliwattHours;
            if (full is > 0 && remaining is >= 0)
            {
                CurrentPercent = (int)Math.Round(remaining.Value * 100.0 / full.Value);
            }
        }
        catch
        {
            // No battery present (desktop PC) — leave the last known value.
        }

        BatteryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _aggregateBattery.ReportUpdated -= OnReportUpdated;
}
