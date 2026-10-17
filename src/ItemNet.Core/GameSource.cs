using ItemNet.Core.FileSystem;
using ItemNet.Core.Mo2;
using ItemNet.Core.Plugins;

namespace ItemNet.Core;

/// <summary>
/// Where the game's files and load order come from. Two ways in:
/// <list type="bullet">
/// <item>Launched from MO2 (or Vortex, or a plain install): the game's Data folder as this process
/// sees it, with plugins.txt from %LOCALAPPDATA% (usvfs redirects both).</item>
/// <item>An MO2 instance and profile read straight from disk, without the VFS.</item>
/// </list>
/// </summary>
public sealed class GameSource : IDisposable
{
    public required string Description { get; init; }
    public required string GamePath { get; init; }
    public required IndexedFileSystem Files { get; init; }
    public required GameData Game { get; init; }
    /// <summary>Data-relative folders lowest priority first, for finding loose files outside the index.</summary>
    public required IReadOnlyList<string> DataRootsLowestFirst { get; init; }
    /// <summary>Where new loose files belong: overwrite for an MO2 profile, Data when the VFS routes writes.</summary>
    public required string WriteDataRoot { get; init; }

    /// <summary>The MO2 profile, when read straight from an instance.</summary>
    public Mo2Profile? Profile { get; init; }

    public string GameDataPath => Path.Combine(GamePath, "Data");

    public static GameSource FromMo2(string instanceDirOrIni, string? profileName, Action<string>? progress = null)
    {
        var ini = Mo2Instance.FindIni(instanceDirOrIni) ?? throw new FileNotFoundException($"No ModOrganizer.ini found for {instanceDirOrIni}");
        var instance = Mo2Instance.Load(ini);
        var profile = instance.OpenProfile(profileName);

        progress?.Invoke("Indexing mod files…");
        var fs = IndexedFileSystem.ForMo2Profile(profile);
        var active = GameData.ReadActivePlugins(profile.PluginsTxt, Path.Combine(instance.GamePath, "Skyrim.ccc"), fs);
        progress?.Invoke("Indexing archives…");
        fs.IndexArchives(active, profile.GameIni());
        progress?.Invoke("Reading plugins…");
        var game = GameData.Load(active, fs);

        var roots = new List<string> { instance.GameDataPath };
        roots.AddRange(profile.EnabledModsHighestFirst().Reverse().Select(m => Path.Combine(instance.ModsDirectory, m)));
        roots.Add(instance.OverwriteDirectory);
        return new GameSource
        {
            Description = $"MO2 profile '{profile.Name}' ({instance.BaseDirectory})",
            GamePath = instance.GamePath, Files = fs, Game = game,
            DataRootsLowestFirst = roots, WriteDataRoot = instance.OverwriteDirectory, Profile = profile,
        };
    }

    public static GameSource FromGameFolder(string gamePath, Action<string>? progress = null)
    {
        var data = Path.Combine(gamePath, "Data");
        if (!File.Exists(Path.Combine(data, "Skyrim.esm"))) throw new FileNotFoundException($"Skyrim.esm not found in {data}");
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pluginsTxt = new[] { "Skyrim Special Edition", "Skyrim Special Edition GOG", "Skyrim Special Edition EPIC" }
            .Select(d => Path.Combine(appData, d, "plugins.txt")).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("plugins.txt not found under %LOCALAPPDATA%");
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var gameIni = Path.Combine(docs, "My Games", "Skyrim Special Edition", "Skyrim.ini");

        progress?.Invoke("Indexing Data…");
        var fs = new IndexedFileSystem([(data, "Data")]);
        var active = GameData.ReadActivePlugins(pluginsTxt, Path.Combine(gamePath, "Skyrim.ccc"), fs);
        progress?.Invoke("Indexing archives…");
        fs.IndexArchives(active, gameIni);
        progress?.Invoke("Reading plugins…");
        var game = GameData.Load(active, fs);
        return new GameSource
        {
            Description = $"Game folder {gamePath}",
            GamePath = gamePath, Files = fs, Game = game,
            DataRootsLowestFirst = [data], WriteDataRoot = data,
        };
    }

    /// <summary>The Steam install, if it can be found from the registry.</summary>
    public static string? DetectGamePath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition", @"SOFTWARE\Bethesda Softworks\Skyrim Special Edition" })
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
            if (k?.GetValue("installed path") is string p && File.Exists(Path.Combine(p, "Data", "Skyrim.esm"))) return p.TrimEnd('\\');
        }
        return null;
    }

    public void Dispose() => Game.Dispose();
}
