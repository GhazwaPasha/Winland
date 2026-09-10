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
public sealed record AppSettings(
    bool IsPinned,
    bool StartAtStartup = false,
    bool StartMinimized = false,
    bool BlackMode = false,
    /// <summary>
    /// OpenWeatherMap API key override for the (currently detached) Weather
    /// module — see WeatherService's class doc and STORE_SUBMISSION.md's
    /// "Weather module — detached, not deleted" section. No Settings UI
    /// writes this today; the field stays so any value a user already saved
    /// survives round-tripping settings.json, and so Weather's future
    /// re-enable doesn't need a settings-schema change.
    /// </summary>
    string WeatherApiKey = "")
{
    /// <summary>First-run default, used only when no settings file exists yet.</summary>
    public static AppSettings Default { get; } = new(IsPinned: true);
}
