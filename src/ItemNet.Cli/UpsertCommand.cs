using ItemNet.Core.Items;
using ItemNet.Core.SkyrimNet;
using ItemNet.Render.D3D;
using ItemNet.Render.Scene;

namespace ItemNet.Cli;

public static partial class Commands
{
    /// <summary>
    /// Renders an item and writes it as a locked OmniSight row. Without --live it only accepts a DB
    /// and image folder outside the MO2 instance, i.e. a copy.
    /// </summary>
    public static int Upsert(CliOptions opts)
    {
        using var s = Session.Open(opts, verbose: false);
        var armo = ResolveArmor(s, opts.Positional.FirstOrDefault() ?? throw new ArgumentException("item required"));
        var dbPath = Path.GetFullPath(opts.Get("db") ?? throw new ArgumentException("--db required"));
        var paths = new SkyrimNetPaths(s.Source);
        var imageDir = Path.GetFullPath(opts.Get("images", paths.ImageWriteDirectory));
        var live = opts.Has("live");
        var mo2Base = s.Source.Profile?.Instance.BaseDirectory ?? s.Source.GamePath;
        if (!live && (dbPath.StartsWith(mo2Base, StringComparison.OrdinalIgnoreCase) || imageDir.StartsWith(mo2Base, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Refusing to write inside the MO2 instance without --live; pass a DB copy and --images <dir>.");
        if (live && SkyrimNetPaths.GameIsRunning())
            throw new InvalidOperationException("Skyrim is running; close it before writing to the live DB.");

        var description = opts.Get("description", "ItemNet test description.");
        var suggested = opts.Get("name", "ItemNet Test Name");
        var gender = opts.Has("male") ? ItemGender.Male : ItemGender.Female;
        var runtimeId = s.Game.ToRuntimeFormId(armo.FormKey) ?? throw new InvalidOperationException("Item's plugin is not active");

        using var renderer = new ItemRenderer(s.Files);
        var plan = RenderPlanner.Plan(s.Game, armo, gender);
        var scene = SceneBuilder.Build(plan, s.Files, new SceneOptions());
        var views = renderer.Render(scene, RenderSettings.ApparelViews, new RenderSettings(), clay: false);

        Directory.CreateDirectory(imageDir);
        var visible = new List<string>();
        long bytes = 0;
        for (var i = 0; i < views.Count; i++)
        {
            var name = SkyrimNetPaths.ImageFileName(runtimeId, gender, i);
            var png = views[i].ToPng();
            File.WriteAllBytes(Path.Combine(imageDir, name), png);
            bytes += png.Length;
            visible.Add(paths.GameVisibleImagePath(name));
        }

        if (live)
        {
            var backup = $"{dbPath}.itemnet-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(dbPath, backup);
            Console.WriteLine($"Backed up DB to {backup}");
        }
        using var db = new OmniSightDb(dbPath, readOnly: false);
        var before = db.GetItem(runtimeId, gender);
        var outcome = db.Upsert(new OmniSightWrite(runtimeId, gender, plan.ItemName, suggested, description,
            "ItemNet:test", visible, views[0].Width, views[0].Height, bytes,
            ContentHash.Compute(s.Game, armo, gender == ItemGender.Female)));
        var after = db.GetItem(runtimeId, gender)!;
        Console.WriteLine($"{OmniSightDb.MakeIdentifier(runtimeId, gender)} {plan.ItemName}: {outcome}");
        Console.WriteLine($"  before: model='{before?.DescriptionModel}' locked={before?.Locked} hash={before?.ContentHash} name='{before?.SuggestedName}'");
        Console.WriteLine($"  after:  model='{after.DescriptionModel}' locked={after.Locked} hash={after.ContentHash} name='{after.SuggestedName}'");
        Console.WriteLine($"  desc:   {after.Description}");
        return 0;
    }
}
