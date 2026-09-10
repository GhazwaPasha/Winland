namespace Winland.Services;

/// <summary>Registers/unregisters Winland to launch automatically when the user signs in.</summary>
public interface IStartupService
{
    /// <summary>
    /// Makes the current process's exe the Run-key target when
    /// <paramref name="enabled"/> is true, removing the entry otherwise.
    /// Safe (and cheap) to call every launch regardless of whether the
    /// value actually changed — see <see cref="StartupService"/>.
    /// </summary>
    void SetEnabled(bool enabled);
}
