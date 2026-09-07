namespace Winland.Services;

public sealed record ClaudeUsageSnapshot(
    int WeeklyPercent,
    int FiveHourPercent,
    string WeeklyResetText,
    string FiveHourResetText,
    bool IsWorking);

/// <summary>
/// Seam for the AI tab's usage numbers. There is no public local API for a
/// Claude account's usage percentages, so this is mocked for now — swap in
/// a real implementation here (e.g. reading from a local Claude Code/desktop
/// session or an authenticated usage endpoint) without touching the UI.
/// </summary>
public interface IClaudeUsageProvider
{
    ClaudeUsageSnapshot GetSnapshot();
}
