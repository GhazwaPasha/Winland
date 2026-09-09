namespace Winland.ViewModels;

/// <summary>
/// One flat row in the AI tab's active-sessions list — "&lt;ProjectName&gt; -
/// &lt;SessionLabel&gt;" (project name bold, in the XAML) rather than a
/// project heading with an indented list underneath; every row names its
/// own project so several projects' sessions can sit in the same list
/// without a grouping wrapper. A display-ready projection of
/// <see cref="Services.ClaudeCodeSessionInfo"/> — SessionLabel already
/// resolved to the session's real title or its auto-derived short name,
/// IsActive already thresholded from LastActivityUtc — see
/// NotchViewModel.RefreshClaudeActivity for both. Purely a presentation
/// concern, which is why it lives here rather than in Models alongside the
/// real domain records.
/// </summary>
public sealed record ClaudeSessionRow(string ProjectName, string SessionLabel, string DurationText, bool IsActive, string? KindBadge)
{
    public bool HasKindBadge => !string.IsNullOrEmpty(KindBadge);
}
