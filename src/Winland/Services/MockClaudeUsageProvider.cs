namespace Winland.Services;

/// <summary>
/// Demo data matching the design prototype's hard-coded values exactly
/// (62% weekly / 42% five-hour). Marked mock so it's obvious at a glance
/// this isn't wired to a real account.
/// </summary>
public sealed class MockClaudeUsageProvider : IClaudeUsageProvider
{
    public ClaudeUsageSnapshot GetSnapshot() => new(
        WeeklyPercent: 62,
        FiveHourPercent: 42,
        WeeklyResetText: "Resets in 3d 4h",
        FiveHourResetText: "Resets in 2h 15m",
        IsWorking: true);
}
