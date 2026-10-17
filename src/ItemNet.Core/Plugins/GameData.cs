using System.Diagnostics;
using ItemNet.Core.FileSystem;
using ItemNet.Core.Mo2;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace ItemNet.Core.Plugins;

public sealed record PluginInfo(
    ModKey ModKey,
    string Path,
    bool IsLight,
    bool IsMaster,
    /// <summary>Full plugins: 0x00-0xFD. Light plugins: index within the FE space (0x000-0xFFF).</summary>
    int RuntimeIndex,
    ISkyrimModGetter Mod)
{
    public string RuntimePrefix => IsLight ? $"FE{RuntimeIndex:X3}" : $"{RuntimeIndex:X2}";
}

/// <summary>The active load order, its link cache, and runtime FormID mapping.</summary>
public sealed class GameData : IDisposable
{
    private static readonly string[] Implicit =
        ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    private readonly Dictionary<ModKey, PluginInfo> _byKey = new();
    private readonly Dictionary<uint, PluginInfo> _byRuntimePrefix = new();

    public IReadOnlyList<PluginInfo> Plugins { get; }
    public ILinkCache<ISkyrimMod, ISkyrimModGetter> LinkCache { get; }
    public ILoadOrder<IModListing<ISkyrimModGetter>> LoadOrder { get; }
    public IReadOnlyList<string> MissingPlugins { get; }
    public TimeSpan LoadTime { get; }

    private GameData(List<PluginInfo> plugins, List<string> missing, TimeSpan time)
    {
        Plugins = plugins;
        MissingPlugins = missing;
        LoadTime = time;
        foreach (var p in plugins)
        {
            _byKey[p.ModKey] = p;
            _byRuntimePrefix[p.IsLight ? 0xFE000000u | ((uint)p.RuntimeIndex << 12) : (uint)p.RuntimeIndex << 24] = p;
        }
        LoadOrder = new LoadOrder<IModListing<ISkyrimModGetter>>(
            plugins.Select(p => new ModListing<ISkyrimModGetter>(p.Mod, enabled: true)));
        LinkCache = LoadOrder.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
    }

    /// <summary>
    /// Active plugins in the order the engine loads them: the base masters, then Creation Club
    /// content listed in Skyrim.ccc, then plugins.txt.
    /// </summary>
    public static List<string> ReadActivePlugins(string pluginsTxt, string cccPath, IGameFileSystem fs)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name)
        {
            if (fs.Exists(name) && seen.Add(name)) order.Add(name);
        }

        foreach (var name in Implicit) Add(name);
        if (File.Exists(cccPath))
            foreach (var line in File.ReadLines(cccPath))
                if (line.Trim() is { Length: > 0 } name) Add(name);
        foreach (var raw in File.ReadLines(pluginsTxt))
        {
            var line = raw.Trim();
            if (line.StartsWith('*')) Add(line[1..]);
        }
        return order;
    }

    public static GameData Load(IReadOnlyList<string> activePlugins, IGameFileSystem fs)
    {
        var sw = Stopwatch.StartNew();
        var plugins = new List<PluginInfo>();
        var missing = new List<string>();
        int full = 0, light = 0;
        foreach (var name in activePlugins)
        {
            var path = fs.GetLoosePath(name);
            if (path is null) { missing.Add(name); continue; }
            var modKey = ModKey.FromFileName(name);
            var mod = SkyrimMod.CreateFromBinaryOverlay(new ModPath(modKey, path), SkyrimRelease.SkyrimSE);
            var flags = mod.ModHeader.Flags;
            var isLight = modKey.Type == ModType.Light || flags.HasFlag(SkyrimModHeader.HeaderFlag.Small);
            var isMaster = modKey.Type != ModType.Plugin || flags.HasFlag(SkyrimModHeader.HeaderFlag.Master);
            plugins.Add(new PluginInfo(modKey, path, isLight, isMaster, isLight ? light++ : full++, mod));
        }
        return new GameData(plugins, missing, sw.Elapsed);
    }

    public PluginInfo? GetPlugin(ModKey key) => _byKey.GetValueOrDefault(key);

    /// <summary>The FormID the running game assigns to a record, as SkyrimNet keys it.</summary>
    public uint? ToRuntimeFormId(FormKey key)
    {
        if (!_byKey.TryGetValue(key.ModKey, out var p)) return null;
        return p.IsLight
            ? 0xFE000000u | ((uint)p.RuntimeIndex << 12) | (key.ID & 0xFFF)
            : ((uint)p.RuntimeIndex << 24) | (key.ID & 0xFFFFFF);
    }

    public FormKey? FromRuntimeFormId(uint id)
    {
        if ((id >> 24) == 0xFE)
            return _byRuntimePrefix.TryGetValue(id & 0xFFFFF000, out var l) && l.IsLight
                ? new FormKey(l.ModKey, id & 0xFFF) : null;
        return _byRuntimePrefix.TryGetValue(id & 0xFF000000, out var p) && !p.IsLight
            ? new FormKey(p.ModKey, id & 0xFFFFFF) : null;
    }

    public void Dispose()
    {
        foreach (var p in Plugins) (p.Mod as IDisposable)?.Dispose();
    }
}
