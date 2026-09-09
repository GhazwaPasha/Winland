namespace Winland.Services;

/// <summary>
/// No real usage source exists (see IClaudeUsageProvider) — this used to
/// return the design prototype's hard-coded demo numbers (62%/42%) forever,
/// which reads as live data even though it never actually moves. Reporting
/// an honest empty/disconnected state instead: 0% rings (an empty ring
/// reads as "no data", not as "you're fine"), explicit "not connected" text
/// where a reset countdown would be, and IsWorking always false — there's
/// no real signal for that either, so no false "Working…" pulse.
/// </summary>
public sealed class MockClaudeUsageProvider : IClaudeUsageProvider
{
    public ClaudeUsageSnapshot GetSnapshot() => new(
        WeeklyPercent: 0,
        FiveHourPercent: 0,
        WeeklyResetText: "Not connected",
        FiveHourResetText: "No account linked",
        IsWorking: false);
}
