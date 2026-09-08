using Winland.Models;

namespace Winland.Services;

/// <summary>Persists app preferences (pin state, etc.) across restarts.</summary>
public interface IAppSettingsService
{
    AppSettings Load();

    void Save(AppSettings settings);
}
