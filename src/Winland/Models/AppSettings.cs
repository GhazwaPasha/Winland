namespace Winland.Models;

/// <summary>
/// Persisted app preferences, round-tripped through <see cref="Services.AppSettingsService"/>.
/// A record loaded via System.Text.Json's parameterized-constructor support —
/// any property missing from an older settings.json on disk (e.g. one saved
/// before StartAtStartup/StartMinimized/BlackMode existed) comes back as that
/// property's plain default(bool), which is false for all three, so an
/// upgrade never crashes and just leaves the new toggles off until the user
/// opts in.
/// </summary>
public sealed record AppSettings(bool IsPinned, bool StartAtStartup = false, bool StartMinimized = false, bool BlackMode = false)
{
    /// <summary>First-run default, used only when no settings file exists yet.</summary>
    public static AppSettings Default { get; } = new(IsPinned: true);
}
