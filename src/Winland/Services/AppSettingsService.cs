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

    public AppSettings Load()
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
