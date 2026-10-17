using System.Text;

namespace ItemNet.Core.Mo2;

/// <summary>
/// An MO2 instance and one of its profiles, read straight from disk (no usvfs needed).
/// </summary>
public sealed class Mo2Instance
{
    public required string IniPath { get; init; }
    public required string BaseDirectory { get; init; }
    public required string GamePath { get; init; }
    public required string ModsDirectory { get; init; }
    public required string ProfilesDirectory { get; init; }
    public required string OverwriteDirectory { get; init; }
    public required string SelectedProfile { get; init; }

    public string GameDataPath => Path.Combine(GamePath, "Data");

    public static Mo2Instance Load(string iniPath)
    {
        var ini = IniFile.Load(iniPath);
        string Get(string key, string fallback = "") =>
            ini.Get("General", key) ?? ini.Get("Settings", key) ?? fallback;

        var iniDir = Path.GetDirectoryName(Path.GetFullPath(iniPath))!;
        var baseDir = Get("base_directory", iniDir);
        string Dir(string key, string leaf)
        {
            var v = Get(key);
            if (string.IsNullOrEmpty(v)) return Path.Combine(baseDir, leaf);
            return Path.GetFullPath(v.Replace("%BASE_DIR%", baseDir, StringComparison.OrdinalIgnoreCase));
        }

        return new Mo2Instance
        {
            IniPath = iniPath,
            BaseDirectory = Path.GetFullPath(baseDir),
            GamePath = Path.GetFullPath(Get("gamePath")),
            ModsDirectory = Dir("mod_directory", "mods"),
            ProfilesDirectory = Dir("profiles_directory", "profiles"),
            OverwriteDirectory = Dir("overwrite_directory", "overwrite"),
            SelectedProfile = Get("selected_profile", "Default"),
        };
    }

    /// <summary>
    /// Finds the ModOrganizer.ini for an instance: either inside the given directory (portable)
    /// or under %LOCALAPPDATA%\ModOrganizer\* whose base_directory matches it.
    /// </summary>
    public static string? FindIni(string instanceOrBaseDir)
    {
        var direct = Path.Combine(instanceOrBaseDir, "ModOrganizer.ini");
        if (File.Exists(direct)) return direct;
        if (File.Exists(instanceOrBaseDir) && instanceOrBaseDir.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            return instanceOrBaseDir;

        var want = Path.GetFullPath(instanceOrBaseDir).TrimEnd('\\', '/');
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
        if (!Directory.Exists(root)) return null;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var ini = Path.Combine(dir, "ModOrganizer.ini");
            if (!File.Exists(ini)) continue;
            var parsed = IniFile.Load(ini);
            var baseDir = parsed.Get("Settings", "base_directory") ?? parsed.Get("General", "base_directory") ?? dir;
            if (string.Equals(Path.GetFullPath(baseDir).TrimEnd('\\', '/'), want, StringComparison.OrdinalIgnoreCase))
                return ini;
        }
        return null;
    }

    public Mo2Profile OpenProfile(string? name = null) => new(this, name ?? SelectedProfile);
}

public sealed class Mo2Profile
{
    public Mo2Instance Instance { get; }
    public string Name { get; }
    public string Directory { get; }
    public bool LocalSettings { get; }

    internal Mo2Profile(Mo2Instance instance, string name)
    {
        Instance = instance;
        Name = name;
        Directory = Path.Combine(instance.ProfilesDirectory, name);
        if (!System.IO.Directory.Exists(Directory))
            throw new DirectoryNotFoundException($"MO2 profile not found: {Directory}");
        var settings = Path.Combine(Directory, "settings.ini");
        LocalSettings = File.Exists(settings) &&
            string.Equals(IniFile.Load(settings).Get("General", "LocalSettings"), "true", StringComparison.OrdinalIgnoreCase);
    }

    public string PluginsTxt => Path.Combine(Directory, "plugins.txt");
    public string LoadOrderTxt => Path.Combine(Directory, "loadorder.txt");

    /// <summary>The game INI that applies to this profile (profile-local or the user's Documents one).</summary>
    public string GameIni(string fileName = "skyrim.ini")
    {
        if (LocalSettings) return Path.Combine(Directory, fileName);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "My Games", "Skyrim Special Edition", fileName);
    }

    /// <summary>Enabled mods, highest priority first (the order modlist.txt stores them in).</summary>
    public IReadOnlyList<string> EnabledModsHighestFirst()
    {
        var result = new List<string>();
        foreach (var raw in File.ReadLines(Path.Combine(Directory, "modlist.txt")))
        {
            var line = raw.Trim();
            if (line.Length < 2 || line[0] == '#') continue;
            if (line[0] != '+') continue; // '-' disabled, '*' unmanaged (DLC/CC live in the game Data folder)
            var name = line[1..];
            if (name.EndsWith("_separator", StringComparison.Ordinal)) continue;
            result.Add(name);
        }
        return result;
    }
}

/// <summary>Minimal reader for MO2's QSettings-style INI files.</summary>
public sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public static IniFile Load(string path)
    {
        var ini = new IniFile();
        var section = "";
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            if (line[0] == '[' && line[^1] == ']') { section = line[1..^1]; continue; }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = Unquote(line[(eq + 1)..].Trim());
            if (!ini._sections.TryGetValue(section, out var dict))
                ini._sections[section] = dict = new(StringComparer.OrdinalIgnoreCase);
            dict[key] = value;
        }
        return ini;
    }

    public string? Get(string section, string key) =>
        _sections.TryGetValue(section, out var d) && d.TryGetValue(key, out var v) ? v : null;

    private static string Unquote(string v)
    {
        if (v.StartsWith("@ByteArray(", StringComparison.Ordinal) && v.EndsWith(')'))
            v = v["@ByteArray(".Length..^1];
        else if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
            v = v[1..^1];
        return v.Replace(@"\\", @"\");
    }
}
