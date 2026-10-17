using System.Diagnostics;
using ItemNet.Core.Mo2;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace ItemNet.Core.FileSystem;

/// <summary>
/// The game's view of Data built from one or more folders, lowest priority first (later folders
/// win). For an MO2 profile that is game Data, each enabled mod, then overwrite; when launched
/// through MO2 it is just Data, which usvfs already merges. Archives are indexed separately and
/// lose to any loose file.
/// </summary>
public sealed class IndexedFileSystem : IGameFileSystem
{
    // Only these subtrees are indexed; everything else the tool needs lives at the Data root.
    private static readonly string[] IndexedRoots = ["meshes", "textures", "strings"];

    private readonly Dictionary<string, LooseFile> _loose = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ArchivedFile> _archived = new(StringComparer.Ordinal);
    private readonly List<string> _archivesLoaded = [];

    public IReadOnlyList<string> ArchivesLoaded => _archivesLoaded;
    public TimeSpan LooseIndexTime { get; private set; }
    public TimeSpan ArchiveIndexTime { get; private set; }
    public int LooseCount => _loose.Count;
    public int ArchivedCount => _archived.Count;

    private readonly record struct LooseFile(string FullPath, string Origin);
    // Files from one archive share its stream, so reads lock on the reader.
    private readonly record struct ArchivedFile(IArchiveFile File, IArchiveReader Reader, string ArchiveName);

    /// <param name="rootsLowestFirst">Folders laid over each other, with a label for each.</param>
    public IndexedFileSystem(IEnumerable<(string path, string origin)> rootsLowestFirst)
    {
        var sw = Stopwatch.StartNew();
        foreach (var (path, origin) in rootsLowestFirst) AddLooseTree(path, origin);
        LooseIndexTime = sw.Elapsed;
    }

    public static IndexedFileSystem ForMo2Profile(Mo2Profile profile)
    {
        var roots = new List<(string, string)> { (profile.Instance.GameDataPath, "<game Data>") };
        roots.AddRange(profile.EnabledModsHighestFirst().Reverse().Select(m => (Path.Combine(profile.Instance.ModsDirectory, m), m)));
        roots.Add((profile.Instance.OverwriteDirectory, "<overwrite>"));
        return new IndexedFileSystem(roots);
    }

    private void AddLooseTree(string root, string origin)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root))
            _loose[IGameFileSystem.Normalize(Path.GetFileName(file))] = new(file, origin);

        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var sub in Directory.EnumerateDirectories(root))
        {
            if (!IndexedRoots.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var file in Directory.EnumerateFiles(sub, "*", opts))
                _loose[IGameFileSystem.Normalize(Path.GetRelativePath(root, file))] = new(file, origin);
        }
    }

    /// <summary>
    /// Indexes archives in the order the game loads them: the INI archive lists first, then each
    /// active plugin's own archives in load order.
    /// </summary>
    public void IndexArchives(IEnumerable<string> activePluginsInOrder, string? gameIni)
    {
        var sw = Stopwatch.StartNew();
        var names = new List<string>();
        if (gameIni is not null && File.Exists(gameIni))
        {
            var parsed = IniFile.Load(gameIni);
            foreach (var key in new[] { "sResourceArchiveList", "sResourceArchiveList2" })
                if (parsed.Get("Archive", key) is { } list)
                    names.AddRange(list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }
        if (names.Count == 0)
        {
            // No INI list to go on: fall back to the vanilla "Skyrim - *.bsa" set.
            names.AddRange(_loose.Keys.Where(k => k.StartsWith("skyrim - ", StringComparison.Ordinal) && k.EndsWith(".bsa", StringComparison.Ordinal)).Order());
        }
        foreach (var plugin in activePluginsInOrder)
        {
            var stem = Path.GetFileNameWithoutExtension(plugin);
            names.Add(stem + ".bsa");
            names.Add(stem + " - Textures.bsa");
        }

        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = GetLoosePath(name);
            if (path is null) continue;
            IArchiveReader reader;
            try { reader = Archive.CreateReader(GameRelease.SkyrimSE, path, new System.IO.Abstractions.FileSystem()); }
            catch (Exception e) { Trace.TraceWarning($"Skipping unreadable archive {name}: {e.Message}"); continue; }
            foreach (var f in reader.Files)
            {
                var key = IGameFileSystem.Normalize(f.Path);
                var slash = key.IndexOf('\\');
                if (slash < 0 || !IndexedRoots.Contains(key[..slash])) continue;
                _archived[key] = new(f, reader, name);
            }
            _archivesLoaded.Add(name);
        }
        ArchiveIndexTime = sw.Elapsed;
    }

    public bool Exists(string path)
    {
        var key = IGameFileSystem.Normalize(path);
        return _loose.ContainsKey(key) || _archived.ContainsKey(key);
    }

    public byte[]? ReadAllBytes(string path)
    {
        var key = IGameFileSystem.Normalize(path);
        if (_loose.TryGetValue(key, out var loose)) return File.ReadAllBytes(loose.FullPath);
        if (_archived.TryGetValue(key, out var arch))
        {
            lock (arch.Reader) return arch.File.GetBytes();
        }
        return null;
    }

    public string? DescribeSource(string path)
    {
        var key = IGameFileSystem.Normalize(path);
        if (_loose.TryGetValue(key, out var loose)) return $"loose: {loose.Origin}";
        if (_archived.TryGetValue(key, out var arch)) return $"bsa: {arch.ArchiveName}";
        return null;
    }

    public string? GetLoosePath(string path) =>
        _loose.TryGetValue(IGameFileSystem.Normalize(path), out var f) ? f.FullPath : null;
}
