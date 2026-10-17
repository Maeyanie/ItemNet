using System.Diagnostics;

namespace ItemNet.Core.SkyrimNet;

/// <summary>Where SkyrimNet keeps its playthrough DBs and OmniSight images for one game source.</summary>
public sealed class SkyrimNetPaths(GameSource source)
{
    private const string PluginDir = @"SKSE\Plugins\SkyrimNet";

    /// <summary>Playthrough DBs, newest first. They live wherever SkyrimNet wrote them (normally overwrite).</summary>
    public IReadOnlyList<FileInfo> Databases()
    {
        var found = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in source.DataRootsLowestFirst)
        {
            var dir = Path.Combine(root, PluginDir, "data");
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "SkyrimNet-*.db"))
                found[Path.GetFileName(f)] = new FileInfo(f);
        }
        return found.Values.OrderByDescending(f => f.LastWriteTimeUtc).ToList();
    }

    /// <summary>Physical folder new images go to: overwrite, as usvfs would put a new file.</summary>
    public string ImageWriteDirectory => Path.Combine(source.WriteDataRoot, PluginDir, "screenshots", "omnisight");

    /// <summary>The path SkyrimNet records in file_path for an image (as seen through the game's Data folder).</summary>
    public string GameVisibleImagePath(string fileName) =>
        Path.Combine(source.GameDataPath, PluginDir, "screenshots/omnisight", fileName);

    /// <summary>Resolves a path SkyrimNet stored in file_path to a file on disk, or null.</summary>
    public string? ResolveImage(string storedPath)
    {
        var name = Path.GetFileName(storedPath.Replace('/', '\\'));
        foreach (var root in source.DataRootsLowestFirst.Reverse())
        {
            var p = Path.Combine(root, PluginDir, "screenshots", "omnisight", name);
            if (File.Exists(p)) return p;
        }
        return File.Exists(storedPath) ? storedPath : null;
    }

    public static string ImageFileName(uint runtimeFormId, ItemGender gender, int view) =>
        $"item_{runtimeFormId:X8}_{(gender == ItemGender.Female ? "female" : "male")}_{view}.png";

    public static bool GameIsRunning() =>
        Process.GetProcessesByName("SkyrimSE").Length > 0 || Process.GetProcessesByName("SkyrimVR").Length > 0;
}
