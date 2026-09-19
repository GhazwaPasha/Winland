using System;
using System.IO;
using System.Text.Json;
using Winland.Models;

namespace Winland.Services;

/// <summary>
/// Reads/writes <c>%LOCALAPPDATA%\Winland\settings.json</c> — same
/// location and read-validate-on-load/best-effort-save shape as
/// <see cref="ShelfStorageService"/>.
/// </summary>
public sealed class AppSettingsService : IAppSettingsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Winland", "settings.json");

    // Read from disk once, then served from memory — AppSettings is an
    // immutable record and this app is single-instance (see
    // SingleInstanceGuard), so nothing else can change the file underneath
    // us. Load() is called from several places on every launch and on each
    // settings toggle; each used to re-read and re-parse the file.
    private readonly object _gate = new();
    private AppSettings? _cached;

    public AppSettings Load()
    {
        lock (_gate)
        {
            return _cached ??= ReadFromDisk();
        }
    }

    private static AppSettings ReadFromDisk()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return AppSettings.Default;
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? AppSettings.Default;
        }
        catch
        {
            // Corrupt or unreadable file — fall back to the first-run default rather than fail startup over it.
            return AppSettings.Default;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            // Cached even if the write below fails — within this session the
            // user's choice should still read back as what they set.
            _cached = settings;
        }

        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings));
        }
        catch
        {
            // Best-effort persistence — a failed save just means the preference resets next launch.
        }
    }
}
