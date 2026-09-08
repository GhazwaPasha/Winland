using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Winland.Services;

/// <summary>
/// Reads/writes <c>%LOCALAPPDATA%\Winland\shelf.json</c> — a flat list of
/// file paths, nothing else. Every path is re-validated against the
/// filesystem on load; anything that no longer resolves (moved or deleted
/// since the last run) is silently dropped rather than shown as a broken
/// chip.
/// </summary>
public sealed class ShelfStorageService : IShelfStorageService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Winland", "shelf.json");

    public IReadOnlyList<string> LoadPaths()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return Array.Empty<string>();
            }

            var json = File.ReadAllText(FilePath);
            var paths = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            return paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        }
        catch
        {
            // Corrupt or unreadable file — start with an empty shelf rather than fail startup over it.
            return Array.Empty<string>();
        }
    }

    public void SavePaths(IEnumerable<string> paths)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(paths.ToList()));
        }
        catch
        {
            // Best-effort persistence — a failed save just means the shelf
            // resets next launch, not worth taking the app down over.
        }
    }
}
