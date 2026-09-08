using System.Collections.Generic;

namespace Winland.Services;

/// <summary>Persists the Shelf tab's dropped-file paths across app restarts.</summary>
public interface IShelfStorageService
{
    IReadOnlyList<string> LoadPaths();

    void SavePaths(IEnumerable<string> paths);
}
