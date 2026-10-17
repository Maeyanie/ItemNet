using System.Numerics;
using ItemNet.Core.FileSystem;
using ItemNet.Core.Items;
using ItemNet.Render.Nif;

namespace ItemNet.Render.Scene;

public enum MannequinStyle
{
    /// <summary>The race's own skin textures, like a BodySlide preview.</summary>
    Textured,
    /// <summary>Flat neutral grey.</summary>
    Clay,
}

public sealed class SceneOptions
{
    /// <summary>BodySlide weight, 0 (thin) to 1 (heavy), for models with a weight slider.</summary>
    public float Weight { get; init; } = 0.5f;
    public MannequinStyle Mannequin { get; init; } = MannequinStyle.Textured;
    public bool ShowMannequin { get; init; } = true;
}

/// <summary>A shape posed in world space, ready to upload.</summary>
public sealed class SceneMesh
{
    public required ModelRole Role { get; init; }
    /// <summary>True for body skin, whether from a body ARMA or baked into the item's NIF.</summary>
    public required bool IsBody { get; init; }
    public required string Name { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector3[] Tangents { get; init; }
    public required Vector3[] Bitangents { get; init; }
    public required Vector2[] Uvs { get; init; }
    public required Vector4[] Colors { get; init; }
    public required int[] Indices { get; init; }
    public required MaterialInfo Material { get; init; }
}

public sealed class Scene
{
    public List<SceneMesh> Meshes { get; } = [];
    public List<string> Warnings { get; } = [];

    public (Vector3 min, Vector3 max)? Bounds(Func<SceneMesh, bool> filter)
    {
        var any = false;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var m in Meshes.Where(filter))
            foreach (var p in m.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); any = true; }
        return any ? (min, max) : null;
    }
}

public static class SceneBuilder
{
    public static Scene Build(RenderPlan plan, IGameFileSystem fs, SceneOptions options)
    {
        var scene = new Scene();
        scene.Warnings.AddRange(plan.Warnings);

        Dictionary<string, Matrix4x4>? skeleton = null;
        if (plan.SkeletonPath is { } skPath && fs.ReadAllBytes(skPath) is { } skData)
            skeleton = NifModel.Load(skPath, skData).NodeTransforms;
        else
            scene.Warnings.Add($"Skeleton not found: {plan.SkeletonPath}; using each NIF's own bones");

        foreach (var model in plan.Models)
        {
            if (!options.ShowMannequin && model.Role != ModelRole.Item) continue;
            var nif = LoadWeighted(model, fs, options.Weight, scene.Warnings);
            if (nif is null) continue;
            scene.Warnings.AddRange(nif.Warnings.Select(w => $"{model.NifPath}: {w}"));

            foreach (var part in nif.Parts)
            {
                if (part.Material.EffectShader) continue; // glows, particles, magic auras
                if (part.Hidden || string.IsNullOrEmpty(part.Material.Textures[0])) continue; // SMP collision, helpers
                var isBody = model.Role != ModelRole.Item || part.Material.SkinShader;
                if (isBody && !options.ShowMannequin) continue;
                var material = ApplyRecordTextures(part, model);
                scene.Meshes.Add(Pose(part, model.Role, isBody, material, nif.NodeTransforms, skeleton));
            }
        }
        return scene;
    }

    private static NifModel? LoadWeighted(ModelRef model, IGameFileSystem fs, float weight, List<string> warnings)
    {
        NifModel? Load(string path) => fs.ReadAllBytes(path) is { } d ? NifModel.Load(path, d) : null;

        if (!model.WeightSlider)
        {
            var single = Load(model.NifPath);
            if (single is null) warnings.Add($"Missing mesh {model.NifPath}");
            return single;
        }

        var heavy = Load(WeightVariant(model.NifPath, 1));
        var thin = Load(WeightVariant(model.NifPath, 0));
        if (heavy is null && thin is null) { warnings.Add($"Missing mesh {model.NifPath} (_0/_1)"); return null; }
        if (heavy is null || thin is null) return heavy ?? thin;

        // Blend shape by shape; BodySlide keeps vertex order identical between _0 and _1.
        var blended = new List<MeshPart>();
        for (var i = 0; i < heavy.Parts.Count; i++)
        {
            var h = heavy.Parts[i];
            var t = thin.Parts.FirstOrDefault(p => p.Name == h.Name && p.Positions.Length == h.Positions.Length);
            if (t is null) { blended.Add(h); continue; }
            var pos = new Vector3[h.Positions.Length];
            for (var v = 0; v < pos.Length; v++) pos[v] = Vector3.Lerp(t.Positions[v], h.Positions[v], weight);
            blended.Add(new MeshPart
            {
                Name = h.Name, Positions = pos, Normals = h.Normals, Tangents = h.Tangents, Bitangents = h.Bitangents,
                Uvs = h.Uvs, Colors = h.Colors, Indices = h.Indices, Material = h.Material, ShapeToRoot = h.ShapeToRoot,
                BoneNames = h.BoneNames, SkinToBone = h.SkinToBone, BoneIndices = h.BoneIndices, BoneWeights = h.BoneWeights,
            });
        }
        var result = new NifModel { Path = heavy.Path, Parts = blended, NodeTransforms = heavy.NodeTransforms };
        result.Warnings.AddRange(heavy.Warnings);
        return result;
    }

    public static string WeightVariant(string nifPath, int weight)
    {
        var stem = nifPath[..^4];
        if (stem.EndsWith("_0", StringComparison.Ordinal) || stem.EndsWith("_1", StringComparison.Ordinal)) stem = stem[..^2];
        return $"{stem}_{weight}.nif";
    }

    private static MaterialInfo ApplyRecordTextures(MeshPart part, ModelRef model)
    {
        var src = part.Material;
        TextureSlots? swap = null;
        if (model.AlternateTextures.TryGetValue(part.Name, out var alt)) swap = alt;
        else if (src.SkinShader && model.SkinTexture is { } skin && model.Role != ModelRole.Head) swap = skin;
        if (swap is null) return src;

        var tex = (string?[])src.Textures.Clone();
        for (var i = 0; i < 8; i++)
            if (!string.IsNullOrEmpty(swap.Paths[i])) tex[i] = swap.Paths[i];
        return new MaterialInfo
        {
            Textures = tex, DoubleSided = src.DoubleSided, ModelSpaceNormals = src.ModelSpaceNormals, SkinShader = src.SkinShader,
            EnvMap = src.EnvMap, Glow = src.Glow, Specular = src.Specular, VertexColors = src.VertexColors, VertexAlpha = src.VertexAlpha,
            EffectShader = src.EffectShader, Alpha = src.Alpha, Glossiness = src.Glossiness, SpecularStrength = src.SpecularStrength,
            SpecularColor = src.SpecularColor, EnvMapScale = src.EnvMapScale, EmissiveColor = src.EmissiveColor,
            EmissiveMultiple = src.EmissiveMultiple, UvOffset = src.UvOffset, UvScale = src.UvScale,
            AlphaMode = src.AlphaMode, AlphaThreshold = src.AlphaThreshold,
        };
    }

    /// <summary>
    /// Bind-pose skinning against the race skeleton, as the game does: each influence maps the
    /// vertex into bone space (NiSkinData) and back out through the skeleton's bone.
    /// </summary>
    private static SceneMesh Pose(MeshPart part, ModelRole role, bool isBody, MaterialInfo material,
        Dictionary<string, Matrix4x4> nifNodes, Dictionary<string, Matrix4x4>? skeleton)
    {
        var n = part.Positions.Length;
        var pos = new Vector3[n];
        var nrm = new Vector3[n];
        var tan = new Vector3[n];
        var bit = new Vector3[n];

        Matrix4x4[]? boneMats = null;
        if (part.IsSkinned && part.SkinToBone is { } s2b)
        {
            boneMats = new Matrix4x4[part.BoneNames!.Length];
            for (var b = 0; b < boneMats.Length; b++)
            {
                var name = part.BoneNames[b];
                var world = skeleton is not null && skeleton.TryGetValue(name, out var sk) ? sk
                    : nifNodes.TryGetValue(name, out var own) ? own : Matrix4x4.Identity;
                boneMats[b] = (b < s2b.Length ? s2b[b] : Matrix4x4.Identity) * world;
            }
        }

        for (var v = 0; v < n; v++)
        {
            Matrix4x4 m;
            if (boneMats is not null)
            {
                m = default;
                float total = 0;
                for (var k = 0; k < 4; k++)
                {
                    var w = part.BoneWeights![v * 4 + k];
                    if (w <= 0) continue;
                    var bi = part.BoneIndices![v * 4 + k];
                    if (bi >= boneMats.Length) continue;
                    m += boneMats[bi] * w;
                    total += w;
                }
                m = total > 0 ? m * (1 / total) : part.ShapeToRoot;
            }
            else m = part.ShapeToRoot;

            pos[v] = Vector3.Transform(part.Positions[v], m);
            nrm[v] = SafeNormalize(Vector3.TransformNormal(part.Normals[v], m));
            if (part.Tangents is { } t) tan[v] = SafeNormalize(Vector3.TransformNormal(t[v], m));
            if (part.Bitangents is { } bt) bit[v] = SafeNormalize(Vector3.TransformNormal(bt[v], m));
        }

        // Meshes lit by model-space normal maps (bodies, heads, hands) carry no vertex normals.
        if (nrm.All(v => v == Vector3.UnitZ)) nrm = SmoothNormals(pos, part.Indices);

        var colors = part.Colors ?? Enumerable.Repeat(Vector4.One, n).ToArray();
        return new SceneMesh
        {
            Role = role, IsBody = isBody, Name = part.Name, Positions = pos, Normals = nrm, Tangents = tan, Bitangents = bit,
            Uvs = part.Uvs, Colors = colors, Indices = part.Indices, Material = material,
        };
    }

    /// <summary>Area-weighted vertex normals, shared across vertices at the same position so UV seams don't show.</summary>
    private static Vector3[] SmoothNormals(Vector3[] pos, int[] indices)
    {
        static (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 100), (int)MathF.Round(p.Y * 100), (int)MathF.Round(p.Z * 100));
        var byPos = new Dictionary<(int, int, int), Vector3>();
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var (a, b, c) = (pos[indices[i]], pos[indices[i + 1]], pos[indices[i + 2]]);
            var face = Vector3.Cross(b - a, c - a); // length = 2x area, so larger faces weigh more
            foreach (var v in new[] { a, b, c })
            {
                var k = Key(v);
                byPos[k] = byPos.GetValueOrDefault(k) + face;
            }
        }
        var result = new Vector3[pos.Length];
        for (var v = 0; v < pos.Length; v++)
            result[v] = SafeNormalize(byPos.GetValueOrDefault(Key(pos[v])));
        return result;
    }

    private static Vector3 SafeNormalize(Vector3 v)
    {
        var l = v.Length();
        return l > 1e-8f ? v / l : Vector3.UnitZ;
    }
}
