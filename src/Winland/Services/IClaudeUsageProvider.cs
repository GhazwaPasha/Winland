namespace Winland.Services;

public sealed record ClaudeUsageSnapshot(
    int WeeklyPercent,
    int FiveHourPercent,
    string WeeklyResetText,
    string FiveHourResetText,
    bool IsWorking);

/// <summary>
/// Seam for the AI tab's usage numbers. There is no public API for a
/// Claude.ai subscription's usage limits (the Anthropic Developer API's
/// usage/cost endpoints cover API-key spend, a different thing, and require
/// a key this app has no business asking for) — so the current
/// implementation reports an explicit "not connected" state rather than
/// invented numbers dressed up as live data. Swap in a real implementation
/// here without touching the UI if a legitimate local/authenticated source
/// ever exists.
/// </summary>
public interface IClaudeUsageProvider
{
    ClaudeUsageSnapshot GetSnapshot();
}
