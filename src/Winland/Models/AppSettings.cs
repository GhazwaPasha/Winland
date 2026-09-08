namespace Winland.Models;

/// <summary>Persisted app preferences — currently just the pin state, but a natural home for more later.</summary>
public sealed record AppSettings(bool IsPinned)
{
    /// <summary>First-run default, used only when no settings file exists yet.</summary>
    public static AppSettings Default { get; } = new(IsPinned: true);
}
