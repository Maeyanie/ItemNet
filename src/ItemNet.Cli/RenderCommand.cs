using System.Diagnostics;
using System.Globalization;
using ItemNet.Core.Items;
using ItemNet.Core.SkyrimNet;
using ItemNet.Render.D3D;
using ItemNet.Render.Scene;

namespace ItemNet.Cli;

public static partial class Commands
{
    public static int Render(CliOptions opts)
    {
        using var s = Session.Open(opts, verbose: false);
        var outDir = opts.Get("out", Path.Combine(Path.GetTempPath(), "ItemNet"));
        Directory.CreateDirectory(outDir);
        var gender = opts.Has("male") ? ItemGender.Male : ItemGender.Female;
        var sceneOpts = new SceneOptions
        {
            Weight = float.Parse(opts.Get("weight", "0.5"), CultureInfo.InvariantCulture),
            Mannequin = opts.Has("clay") ? MannequinStyle.Clay : MannequinStyle.Textured,
            ShowMannequin = !opts.Has("no-mannequin"),
        };
        var settings = new RenderSettings { Size = int.Parse(opts.Get("size", "512"), CultureInfo.InvariantCulture) };

        using var renderer = new ItemRenderer(s.Files);
        Console.WriteLine($"Renderer: {renderer.AdapterDescription}");
        foreach (var spec in opts.Positional)
        {
            var sw = Stopwatch.StartNew();
            var armo = ResolveArmor(s, spec);
            var plan = RenderPlanner.Plan(s.Game, armo, gender, mannequin: sceneOpts.ShowMannequin);
            var scene = SceneBuilder.Build(plan, s.Files, sceneOpts);
            var views = renderer.Render(scene, RenderSettings.ApparelViews, settings, sceneOpts.Mannequin == MannequinStyle.Clay);
            var stem = $"{armo.EditorID ?? armo.FormKey.IDString()}_{gender.ToString().ToLowerInvariant()}{(opts.Has("clay") ? "_clay" : "")}{(opts.Has("no-mannequin") ? "_bare" : "")}";
            foreach (var v in views) File.WriteAllBytes(Path.Combine(outDir, $"{stem}_{v.Label}.png"), v.ToPng());
            var sheet = ContactSheet(views);
            var sheetPath = Path.Combine(outDir, $"{stem}_sheet.png");
            File.WriteAllBytes(sheetPath, sheet);
            Console.WriteLine($"{plan.ItemName}: {scene.Meshes.Count} meshes ({scene.Meshes.Count(m => m.IsBody)} body) in {sw.ElapsedMilliseconds} ms -> {sheetPath}");
            foreach (var w in scene.Warnings) Console.WriteLine($"  warn: {w}");
        }
        foreach (var m in renderer.MissingTextures) Console.WriteLine($"  missing texture: {m}");
        return 0;
    }

    /// <summary>2x2 grid of the four views, for quick review.</summary>
    private static byte[] ContactSheet(List<RenderedView> views)
    {
        var w = views[0].Width;
        var h = views[0].Height;
        var cols = 2;
        var rows = (views.Count + 1) / 2;
        var sheet = new byte[cols * w * rows * h * 4];
        for (var i = 0; i < views.Count; i++)
        {
            int ox = i % cols * w, oy = i / cols * h;
            for (var y = 0; y < h; y++)
                Buffer.BlockCopy(views[i].Rgba, y * w * 4, sheet, ((oy + y) * cols * w + ox) * 4, w * 4);
        }
        return Png.Encode(sheet, cols * w, rows * h);
    }
}
