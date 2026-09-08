using System;

namespace Winland.Services;

public sealed record HeadphoneSnapshot(bool IsConnected, bool IsWireless, int? BatteryPercent);

/// <summary>Whether headphones are the current default audio output, and their battery % if wireless.</summary>
public interface IHeadphoneService
{
    HeadphoneSnapshot Snapshot { get; }

    event EventHandler? Changed;
}
