using System.Globalization;
using ItemNet.Core.Items;
using ItemNet.Core.SkyrimNet;
using ItemNet.Render.Nif;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace ItemNet.Cli;

public static partial class Commands
{
    /// <summary>Finds an armour by runtime FormID ("06000873"), FormKey ("000873:Plugin.esm") or EditorID.</summary>
    public static IArmorGetter ResolveArmor(Session s, string spec)
    {
        if (spec.Length == 8 && uint.TryParse(spec, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rt)
            && s.Game.FromRuntimeFormId(rt) is { } fk && s.Game.LinkCache.TryResolve<IArmorGetter>(fk, out var byRt))
            return byRt;
        if (FormKey.TryFactory(spec, out var key) && s.Game.LinkCache.TryResolve<IArmorGetter>(key, out var byKey))
            return byKey;
        if (s.Game.LinkCache.TryResolve<IArmorGetter>(spec, out var byEdid))
            return byEdid;
        throw new ArgumentException($"No armour found for '{spec}'");
    }

    public static int Plan(CliOptions opts)
    {
        using var s = Session.Open(opts, verbose: false);
        var armo = ResolveArmor(s, opts.Positional.FirstOrDefault() ?? throw new ArgumentException("item required"));
        var gender = opts.Has("male") ? ItemGender.Male : ItemGender.Female;
        opts_verbose = opts.Has("verbose");
        var plan = RenderPlanner.Plan(s.Game, armo, gender);
        Console.WriteLine($"{plan.ItemName} [{armo.FormKey}] {gender}, slots 0x{plan.ItemSlots:X8}, skeleton {plan.SkeletonPath}");
        foreach (var w in plan.Warnings) Console.WriteLine($"  warn: {w}");

        foreach (var m in plan.Models)
        {
            Console.WriteLine($"- {m.Role} {m.NifPath} (weight slider: {m.WeightSlider}, from {m.Source}, alt tex {m.AlternateTextures.Count}, skin tex {(m.SkinTexture is null ? "-" : "yes")})");
            var path = m.WeightSlider ? WeightVariant(m.NifPath, 1) : m.NifPath;
            Describe(s, path);
        }
        if (plan.SkeletonPath is { } sk)
        {
            var data = s.Files.ReadAllBytes(sk);
            if (data is null) Console.WriteLine($"  skeleton missing: {sk}");
            else
            {
                var skel = NifModel.Load(sk, data);
                Console.WriteLine($"- skeleton {sk} [{s.Files.DescribeSource(sk)}]: {skel.NodeTransforms.Count} nodes");
            }
        }
        return 0;
    }

    public static string WeightVariant(string nifPath, int weight)
    {
        var stem = nifPath[..^4];
        if (stem.EndsWith("_0", StringComparison.Ordinal) || stem.EndsWith("_1", StringComparison.Ordinal)) stem = stem[..^2];
        return $"{stem}_{weight}.nif";
    }

    private static bool opts_verbose;

    private static void Describe(Session s, string path)
    {
        var data = s.Files.ReadAllBytes(path);
        if (data is null) { Console.WriteLine($"    MISSING {path}"); return; }
        var model = NifModel.Load(path, data);
        Console.WriteLine($"    {path} [{s.Files.DescribeSource(path)}] {model.Parts.Count} parts, {model.NodeTransforms.Count} nodes");
        foreach (var w in model.Warnings) Console.WriteLine($"    warn: {w}");
        foreach (var p in model.Parts)
        {
            var maxBone = p.BoneIndices is null ? -1 : p.BoneIndices.Max();
            var mat = p.Material;
            Console.WriteLine($"    * '{p.Name}' v={p.Positions.Length} t={p.Indices.Length / 3} " +
                $"{(p.IsSkinned ? $"skinned bones={p.BoneNames!.Length} maxIdx={maxBone} skinToBone={p.SkinToBone?.Length}" : "static")} " +
                $"alpha={mat.AlphaMode}{(mat.SkinShader ? " SKIN" : "")}{(mat.EnvMap ? " env" : "")}{(mat.Glow ? " glow" : "")}{(mat.ModelSpaceNormals ? " msn" : "")}{(mat.EffectShader ? " EFFECT" : "")}");
            Console.WriteLine($"        diffuse={mat.Textures[0]} normal={mat.Textures[1]}");
            if (opts_verbose)
            {
                var avg = p.Colors is null ? "none" : (p.Colors.Aggregate(System.Numerics.Vector4.Zero, (a, c) => a + c) / p.Colors.Length).ToString();
                string Fmt(string? t)
                {
                    if (t is null) return "-";
                    var bytes = s.Files.ReadAllBytes(ItemNet.Render.D3D.TextureCache.Normalize(t));
                    if (bytes is null) return "MISSING";
                    try { var d = ItemNet.Render.D3D.DdsTexture.Parse(bytes); return $"{d.Format} {d.Width}x{d.Height} mips={d.MipLevels}"; }
                    catch (Exception e) { return "ERR " + e.Message; }
                }
                Console.WriteLine($"        vcolFlag={mat.VertexColors} valpha={mat.VertexAlpha} avgColor={avg} alpha={mat.Alpha} spec={mat.Specular}/{mat.SpecularStrength} gloss={mat.Glossiness}");
                Console.WriteLine($"        diffuseFmt={Fmt(mat.Textures[0])} normalFmt={Fmt(mat.Textures[1])}");
                var lens = p.Normals.Select(n => n.Length()).ToArray();
                Console.WriteLine($"        normals: min {lens.Min():F3} max {lens.Max():F3} nan {lens.Count(float.IsNaN)}; tangents {(p.Tangents is null ? "none" : "yes")}; first {p.Normals[0]}");
            }
        }
    }
}
