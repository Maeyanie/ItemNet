using System.IO;
using System.Net.Http;
using System.Text.Json;
using ItemNet.Core;
using ItemNet.Core.Catalog;
using ItemNet.Core.Items;
using ItemNet.Core.SkyrimNet;
using ItemNet.Render.D3D;
using ItemNet.Render.Scene;
using ItemNet.Vision;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace ItemNet.App.Services;

public sealed record SaveResult(string Message, bool WroteSkyrimNet);

/// <summary>Everything loaded for one game source: items, renderer, catalog and the target SkyrimNet DB.</summary>
public sealed class Workspace : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly SemaphoreSlim _renderLock = new(1, 1);
    private readonly HashSet<string> _backedUp = new(StringComparer.OrdinalIgnoreCase);
    private ItemRenderer? _renderer;

    public required GameSource Source { get; init; }
    public required List<ItemEntry> Items { get; init; }
    public required SkyrimNetPaths SkyrimNet { get; init; }
    public required CatalogStore Catalog { get; init; }
    public IReadOnlyList<FileInfo> Databases { get; private set; } = [];
    public FileInfo? TargetDb { get; private set; }
    public Dictionary<(uint, ItemGender), OmniSightItemRow> DbRows { get; private set; } = new();
    public Dictionary<(FormKey, ItemGender), CatalogEntry> CatalogEntries { get; private set; } = new();

    /// <summary>What the title bar shows: the source, and whether MO2's VFS is supplying it.</summary>
    public string SourceLabel { get; private init; } = "";

    public static Workspace Open(AppSettings settings, IProgress<string> progress)
    {
        // Launched from MO2, the VFS already shows exactly what the game sees for the active
        // profile, so that wins over a saved "read the profile directly" choice.
        var underMo2 = AppSettings.RunningUnderMo2();
        var gamePath = string.IsNullOrWhiteSpace(settings.GamePath) ? GameSource.DetectGamePath() ?? "" : settings.GamePath;
        var source = settings.Mode == SourceMode.Mo2Profile && !underMo2
            ? GameSource.FromMo2(settings.Mo2Instance, string.IsNullOrEmpty(settings.Mo2Profile) ? null : settings.Mo2Profile, progress.Report)
            : GameSource.FromGameFolder(gamePath, progress.Report);
        progress.Report("Listing items…");
        var items = ItemIndex.Build(source.Game).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var ws = new Workspace
        {
            SourceLabel = source.Description + (underMo2 ? " (launched from MO2)" : ""),
            Source = source, Items = items, SkyrimNet = new SkyrimNetPaths(source),
            Catalog = new CatalogStore(string.IsNullOrWhiteSpace(settings.CatalogFolder) ? AppSettings.Folder : settings.CatalogFolder),
        };
        ws.RefreshCatalog();
        ws.Databases = ws.SkyrimNet.Databases();
        ws.SelectDatabase(ws.Databases.FirstOrDefault(d => d.Name.Equals(settings.TargetDb, StringComparison.OrdinalIgnoreCase)) ?? ws.Databases.FirstOrDefault());
        return ws;
    }

    public void RefreshCatalog() =>
        CatalogEntries = Catalog.All().ToDictionary(e => (e.Key, e.Gender));

    public void SelectDatabase(FileInfo? db)
    {
        TargetDb = db;
        DbRows = new();
        if (db is null) return;
        using var conn = new OmniSightDb(db.FullName);
        foreach (var row in conn.ReadItems()) DbRows[(row.RuntimeFormId, row.Gender)] = row;
    }

    public OmniSightItemRow? DbRow(ItemEntry e, ItemGender g) =>
        e.RuntimeId is { } id && DbRows.TryGetValue((id, g), out var row) ? row : null;

    public CatalogEntry? CatalogEntry(ItemEntry e, ItemGender g) => CatalogEntries.GetValueOrDefault((e.Key, g));

    public IArmorGetter Armor(ItemEntry e) => Source.Game.LinkCache.Resolve<IArmorGetter>(e.Key);

    /// <summary>Renders the four apparel views. One render at a time: the D3D11 context isn't thread-safe.</summary>
    public async Task<(List<RenderedView> views, List<string> warnings)> RenderAsync(ItemEntry e, ItemGender gender, SceneOptions options, int size, CancellationToken ct)
    {
        await _renderLock.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                _renderer ??= new ItemRenderer(Source.Files);
                var plan = RenderPlanner.Plan(Source.Game, Armor(e), gender, mannequin: options.ShowMannequin);
                var scene = SceneBuilder.Build(plan, Source.Files, options);
                var views = _renderer.Render(scene, RenderSettings.ApparelViews, new RenderSettings { Size = size },
                    clay: options.Mannequin == MannequinStyle.Clay);
                var warnings = scene.Warnings.Concat(_renderer.MissingTextures.Select(t => "Missing texture: " + t)).Distinct().ToList();
                return (views, warnings);
            }, ct);
        }
        finally { _renderLock.Release(); }
    }

    public Task<DescriptionResult> DescribeAsync(ProviderConfig provider, VisionModelConfig model, string prompt,
        IReadOnlyList<RenderedView> views, CancellationToken ct) =>
        new VisionClient(Http).DescribeAsync(provider, model, prompt, views.Select(v => v.ToPng()).ToList(), ct);

    /// <summary>
    /// Saves to the catalog, then to the target SkyrimNet DB as a locked row. Backs the DB up once
    /// per session first.
    /// </summary>
    public SaveResult Save(ItemEntry e, ItemGender gender, string name, string description, string model, bool edited,
        IReadOnlyList<RenderedView> views, string renderSettings)
    {
        // Catalog first: it is the source of truth and doesn't depend on the load order.
        var pngs = views.Select(v => v.ToPng()).ToList();
        var catalogImages = new List<string>();
        for (var i = 0; i < pngs.Count; i++)
        {
            var path = Catalog.ImagePath(e.Key, gender, i);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, pngs[i]);
            catalogImages.Add(path);
        }
        var label = "ItemNet:" + model + (edited ? "+edited" : "");
        Catalog.Upsert(new CatalogEntry(e.Key, gender, e.Name, e.EditorId, name, description, label, edited,
            EntryStatus.Approved, catalogImages, renderSettings, DateTime.UtcNow));
        RefreshCatalog();

        if (TargetDb is null) return new SaveResult("Saved to the ItemNet catalog. No SkyrimNet database selected.", false);
        if (e.RuntimeId is not { } runtimeId) return new SaveResult("Saved to the catalog; the item's plugin isn't active, so SkyrimNet was skipped.", false);

        if (_backedUp.Add(TargetDb.FullName)) OmniSightDb.Backup(TargetDb.FullName);

        Directory.CreateDirectory(SkyrimNet.ImageWriteDirectory);
        var visible = new List<string>();
        for (var i = 0; i < pngs.Count; i++)
        {
            var file = SkyrimNetPaths.ImageFileName(runtimeId, gender, i);
            File.WriteAllBytes(Path.Combine(SkyrimNet.ImageWriteDirectory, file), pngs[i]);
            visible.Add(SkyrimNet.GameVisibleImagePath(file));
        }

        WriteOutcome outcome;
        using (var db = new OmniSightDb(TargetDb.FullName, readOnly: false))
        {
            outcome = db.Upsert(new OmniSightWrite(runtimeId, gender, e.Name, name, description, label, visible,
                views[0].Width, views[0].Height, pngs.Sum(p => (long)p.Length),
                ContentHash.Compute(Source.Game, Armor(e), gender == ItemGender.Female)));
        }
        Catalog.RecordSync(e.Key, gender, TargetDb.FullName, runtimeId);
        SelectDatabase(TargetDb);

        var verb = outcome == WriteOutcome.Inserted ? "Added to" : "Updated in";
        var live = SkyrimNetPaths.GameIsRunning() ? " Skyrim is running: it takes effect at the next cell change or load." : "";
        return new SaveResult($"Saved. {verb} {TargetDb.Name}.{live}", true);
    }

    /// <summary>SkyrimNet's config folder (for importing providers), highest-priority copy first.</summary>
    public string? SkyrimNetConfigDir() =>
        Source.DataRootsLowestFirst.Reverse()
            .Select(r => Path.Combine(r, "SKSE", "Plugins", "SkyrimNet", "config"))
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "Providers.yaml")));

    public static string DescribeRenderSettings(SceneOptions o, int size) =>
        JsonSerializer.Serialize(new { mannequin = o.ShowMannequin ? o.Mannequin.ToString() : "None", weight = o.Weight, size });

    public void Dispose()
    {
        _renderer?.Dispose();
        Catalog.Dispose();
        Source.Dispose();
    }
}
