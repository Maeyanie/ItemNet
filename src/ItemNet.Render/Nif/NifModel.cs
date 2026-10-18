using System.Numerics;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Interfaces;
using NiflySharp.Structs;

namespace ItemNet.Render.Nif;

public enum AlphaMode { Opaque, Test, Blend }

public sealed class MaterialInfo
{
    /// <summary>
    /// BSShaderTextureSet slots: 0 diffuse, 1 normal, 2 glow / skin subsurface, 3 height, 4 env cube,
    /// 5 env mask, 6 multilayer inner, 7 backlight / skin specular. TruePBR reuses them: 2 emissive,
    /// 3 displacement, 5 RMAOS, 6 features 1 (fuzz/coat), 7 features 0 (subsurface).
    /// </summary>
    public string?[] Textures { get; set; } = new string?[8];
    /// <summary>Community Shaders TruePBR material (as PGPatcher/ParallaxGen writes them).</summary>
    public bool Pbr { get; set; }
    /// <summary>TruePBR: multiplies RMAOS red (the NIF's specular strength field).</summary>
    public float PbrRoughnessScale { get; set; } = 1;
    /// <summary>TruePBR: multiplies RMAOS alpha into F0 (the NIF's glossiness field).</summary>
    public float PbrSpecularLevel { get; set; } = 0.04f;
    /// <summary>TruePBR fuzz (cloth sheen): tint and weight, both scaled by the slot 6 texture.</summary>
    public Vector3 FuzzColor { get; set; }
    public float FuzzWeight { get; set; }
    public bool DoubleSided { get; set; }
    public bool ModelSpaceNormals { get; set; }
    public bool SkinShader { get; set; }
    public bool EnvMap { get; set; }
    public bool Glow { get; set; }
    public bool Specular { get; set; }
    public bool VertexColors { get; set; }
    public bool VertexAlpha { get; set; }
    public bool EffectShader { get; set; }
    public float Alpha { get; set; } = 1;
    public float Glossiness { get; set; } = 30;
    public float SpecularStrength { get; set; } = 1;
    public Vector3 SpecularColor { get; set; } = Vector3.One;
    public float EnvMapScale { get; set; } = 1;
    public Vector3 EmissiveColor { get; set; }
    public float EmissiveMultiple { get; set; }
    public Vector2 UvOffset { get; set; }
    public Vector2 UvScale { get; set; } = Vector2.One;
    public AlphaMode AlphaMode { get; set; }
    public float AlphaThreshold { get; set; } = 0.5f;

    public MaterialInfo Clone()
    {
        var copy = (MaterialInfo)MemberwiseClone();
        copy.Textures = (string?[])Textures.Clone();
        return copy;
    }
}

/// <summary>One drawable shape, still in its NIF's own space (unskinned or skin space).</summary>
public sealed class MeshPart
{
    public required string Name { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public Vector3[]? Tangents { get; init; }
    public Vector3[]? Bitangents { get; init; }
    public required Vector2[] Uvs { get; init; }
    public Vector4[]? Colors { get; init; }
    public required int[] Indices { get; init; }
    public required MaterialInfo Material { get; init; }

    /// <summary>NiAVObject hidden flag: SMP collision proxies and editor helpers.</summary>
    public bool Hidden { get; init; }

    /// <summary>Shape-to-NIF-root transform, for unskinned shapes.</summary>
    public Matrix4x4 ShapeToRoot { get; init; } = Matrix4x4.Identity;

    // Skinning: four influences per vertex, indices into BoneNames.
    public string[]? BoneNames { get; init; }
    /// <summary>Per bone: skin space to bone space (NiSkinData bone transform).</summary>
    public Matrix4x4[]? SkinToBone { get; init; }
    public byte[]? BoneIndices { get; init; }
    public float[]? BoneWeights { get; init; }
    public bool IsSkinned => BoneNames is { Length: > 0 } && BoneIndices is not null;
}

public sealed class NifModel
{
    public required string Path { get; init; }
    public required List<MeshPart> Parts { get; init; }
    /// <summary>World (root-relative) transforms of every named node in the file.</summary>
    public required Dictionary<string, Matrix4x4> NodeTransforms { get; init; }
    /// <summary>
    /// The root's "Prn" string extra data: the skeleton bone an unskinned model (backpacks, quivers,
    /// some jewellery) is parented to when worn.
    /// </summary>
    public string? AttachBone { get; init; }
    public List<string> Warnings { get; } = [];

    public static NifModel Load(string path, byte[] data)
    {
        var nif = new NifFile();
        using (var ms = new MemoryStream(data))
        {
            var rc = nif.Load(ms, new NifFileLoadOptions());
            if (rc != 0) throw new InvalidDataException($"Nifly failed to load {path} (code {rc})");
        }

        var nodes = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in nif.GetRootNodes()) CollectNodes(nif, root, Matrix4x4.Identity, nodes);

        var prn = nif.GetRootNodes()
            .SelectMany(r => r.ExtraDataList.GetBlocks(nif))
            .OfType<NiStringExtraData>()
            .FirstOrDefault(e => string.Equals(e.Name?.String, "Prn", StringComparison.OrdinalIgnoreCase))?.StringData?.String;
        var model = new NifModel { Path = path, Parts = [], NodeTransforms = nodes, AttachBone = string.IsNullOrWhiteSpace(prn) ? null : prn };
        foreach (var shape in nif.GetShapes())
        {
            try
            {
                if (ReadShape(nif, shape, model) is { } part) model.Parts.Add(part);
            }
            catch (Exception e)
            {
                model.Warnings.Add($"{NameOf(shape)}: {e.Message}");
            }
        }
        return model;
    }

    internal static string NameOf(INiObject o) => (o as INiNamed)?.Name?.String ?? "";

    private static Matrix4x4 LocalTransform(NiAVObject o) => ToMatrix(o.Rotation, o.Translation, o.Scale);

    internal static Matrix4x4 ToMatrix(Matrix33 r, Vector3 t, float s)
    {
        // Nifly's Matrix33 already holds the row-vector form System.Numerics expects (v' = v R);
        // verified by every influence of a bind-pose vertex landing on the same point.
        return new Matrix4x4(
            r.M11 * s, r.M12 * s, r.M13 * s, 0,
            r.M21 * s, r.M22 * s, r.M23 * s, 0,
            r.M31 * s, r.M32 * s, r.M33 * s, 0,
            t.X, t.Y, t.Z, 1);
    }

    internal static Matrix4x4 ToMatrix(NiTransform t) => ToMatrix(t.Rotation, t.Translation, t.Scale);

    private static void CollectNodes(NifFile nif, NiObject obj, Matrix4x4 parent, Dictionary<string, Matrix4x4> nodes)
    {
        if (obj is not NiAVObject av) return;
        var world = LocalTransform(av) * parent;
        var name = NameOf(av);
        if (name.Length > 0) nodes.TryAdd(name, world);
        if (av is NiNode node)
            foreach (var child in node.Children.GetBlocks(nif))
                if (child is NiObject c) CollectNodes(nif, c, world, nodes);
    }

    private static Matrix4x4 ShapeWorld(NifFile nif, INiShape shape)
    {
        var m = shape is NiAVObject av ? LocalTransform(av) : Matrix4x4.Identity;
        var parent = nif.GetParentNode(shape);
        while (parent is NiAVObject p)
        {
            m *= LocalTransform(p);
            parent = nif.GetParentNode(p);
        }
        return m;
    }

    private static MeshPart? ReadShape(NifFile nif, INiShape shape, NifModel model)
    {
        var name = NameOf(shape);
        Vector3[] pos; Vector3[] nrm; Vector3[]? tan = null, bit = null; Vector2[] uv; Vector4[]? col = null;
        byte[]? bIdx = null; float[]? bW = null;
        List<Triangle> tris;

        if (shape is BSTriShape bs)
        {
            var vd = bs.VertexDataSSE;
            if (vd is null || vd.Count == 0)
            {
                model.Warnings.Add($"{name}: no SSE vertex data ({bs.GetType().Name}, {bs.VertexCount} verts)");
                return null;
            }
            pos = bs is BSDynamicTriShape dyn && dyn.Vertices is { Count: > 0 } dv
                ? dv.Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray()
                : vd.Select(v => v.Vertex).ToArray();
            nrm = bs.HasNormals ? vd.Select(v => Unpack(v.Normal)).ToArray() : new Vector3[pos.Length];
            if (bs.HasTangents)
            {
                tan = vd.Select(v => Unpack(v.Tangent)).ToArray();
                bit = vd.Select(v => new Vector3(v.BitangentX, Unorm8(v.BitangentY), Unorm8(v.BitangentZ))).ToArray();
            }
            uv = bs.HasUVs ? vd.Select(v => new Vector2((float)v.UV.U, (float)v.UV.V)).ToArray() : new Vector2[pos.Length];
            if (bs.HasVertexColors)
                col = vd.Select(v => new Vector4(v.VertexColors.R, v.VertexColors.G, v.VertexColors.B, v.VertexColors.A) / 255f).ToArray();
            if (bs.IsSkinned)
            {
                bIdx = new byte[pos.Length * 4];
                bW = new float[pos.Length * 4];
                for (var i = 0; i < vd.Count; i++)
                    for (var k = 0; k < 4; k++)
                    {
                        bIdx[i * 4 + k] = vd[i].BoneIndices[k];
                        bW[i * 4 + k] = (float)vd[i].BoneWeights[k];
                    }
            }
            tris = bs.Triangles ?? [];
        }
        else if (shape is NiGeometry geo && geo.GeometryData is { } gd)
        {
            pos = gd.Vertices?.ToArray() ?? [];
            nrm = gd.Normals is { Count: > 0 } n ? n.ToArray() : new Vector3[pos.Length];
            if (gd.Tangents is { Count: > 0 } t && gd.Bitangents is { Count: > 0 } b) { tan = t.ToArray(); bit = b.ToArray(); }
            uv = gd.UVSets is { Count: > 0 } u ? u.Take(pos.Length).Select(x => new Vector2(x.U, x.V)).ToArray() : new Vector2[pos.Length];
            if (gd.VertexColors is { Count: > 0 } c) col = c.Select(x => new Vector4(x.R, x.G, x.B, x.A)).ToArray();
            tris = gd.Triangles ?? shape.Triangles ?? [];
        }
        else return null;

        if (pos.Length == 0 || tris.Count == 0) return null;

        var material = ReadMaterial(nif, shape);
        string[]? boneNames = null;
        Matrix4x4[]? skinToBone = null;
        if (shape.IsSkinned && shape.SkinInstanceRef is { } skinRef && nif.GetBlock<NiSkinInstance>(skinRef.Index) is { } skin)
        {
            boneNames = skin.Bones.GetBlocks(nif).Select(b => b is null ? "" : NameOf(b)).ToArray();
            var data = nif.GetBlock(skin.Data);
            skinToBone = data?.BoneList?.Select(b => ToMatrix(b.SkinTransform)).ToArray();
            if (shape is not BSTriShape && data?.BoneList is { } bl)
            {
                // Legacy geometry keeps weights per bone in NiSkinData; fold them into 4 influences.
                var infl = new List<(byte bone, float w)>[pos.Length];
                for (var bi = 0; bi < bl.Count; bi++)
                    foreach (var vw in bl[bi].VertexWeights ?? [])
                        if (vw.Index < pos.Length) (infl[vw.Index] ??= []).Add(((byte)bi, vw.Weight));
                bIdx = new byte[pos.Length * 4];
                bW = new float[pos.Length * 4];
                for (var v = 0; v < pos.Length; v++)
                {
                    var top = (infl[v] ?? []).OrderByDescending(x => x.w).Take(4).ToList();
                    for (var k = 0; k < top.Count; k++) { bIdx[v * 4 + k] = top[k].bone; bW[v * 4 + k] = top[k].w; }
                }
            }
        }

        var indices = new int[tris.Count * 3];
        for (var i = 0; i < tris.Count; i++)
        {
            indices[i * 3] = tris[i].V1;
            indices[i * 3 + 1] = tris[i].V2;
            indices[i * 3 + 2] = tris[i].V3;
        }

        return new MeshPart
        {
            Name = name, Positions = pos, Normals = nrm, Tangents = tan, Bitangents = bit, Uvs = uv, Colors = col,
            Indices = indices, Material = material, ShapeToRoot = ShapeWorld(nif, shape),
            Hidden = shape is NiAVObject av && (av.Flags_ui & 1) != 0,
            BoneNames = boneNames, SkinToBone = skinToBone, BoneIndices = bIdx, BoneWeights = bW,
        };
    }

    private static Vector3 Unpack(ByteVector3 v) => new(Unorm8(v.X), Unorm8(v.Y), Unorm8(v.Z));

    // Normals, tangents and bitangents are stored as unsigned bytes mapping 0..255 to -1..1.
    // NiflySharp hands the raw byte over as an sbyte, so reinterpret it before scaling; dividing
    // the signed value directly scrambled every vertex normal and tangent frame.
    private static float Unorm8(sbyte b) => unchecked((byte)b) / 127.5f - 1;

    private static MaterialInfo ReadMaterial(NifFile nif, INiShape shape)
    {
        var m = new MaterialInfo();
        var shader = nif.GetShader(shape);
        if (shader is not null)
        {
            m.EffectShader = shader is BSEffectShaderProperty;
            m.DoubleSided = shader.DoubleSided;
            m.ModelSpaceNormals = shader.ModelSpace;
            m.SkinShader = shader.IsTypeSkinTint || shader.IsTypeFaceTint;
            m.EnvMap = shader.HasEnvironmentMapping || shader.IsTypeEnvironmentMap;
            m.Glow = shader.HasGlowmap || shader.IsTypeGlow;
            m.Specular = shader.HasSpecular;
            m.VertexColors = shader.HasVertexColors;
            m.VertexAlpha = shader.HasVertexAlpha;
            m.Alpha = shader.Alpha;
            m.Glossiness = shader.Glossiness;
            m.SpecularStrength = shader.SpecularStrength;
            m.SpecularColor = new Vector3(shader.SpecularColor.R, shader.SpecularColor.G, shader.SpecularColor.B);
            m.EnvMapScale = shader.EnvironmentMapScale;
            m.EmissiveColor = new Vector3(shader.EmissiveColor.R, shader.EmissiveColor.G, shader.EmissiveColor.B);
            m.EmissiveMultiple = shader.EmissiveMultiple;
            m.UvOffset = new Vector2(shader.UVOffset.U, shader.UVOffset.V);
            m.UvScale = new Vector2(shader.UVScale.U, shader.UVScale.V);
            if (shader.HasTextureSet && nif.GetBlock(shader.TextureSetRef) is BSShaderTextureSet ts)
                for (var i = 0; i < Math.Min(8, ts.Textures.Count); i++)
                    m.Textures[i] = string.IsNullOrWhiteSpace(ts.Textures[i]?.Content) ? null : ts.Textures[i].Content;
            else if (shader is BSEffectShaderProperty fx)
                m.Textures[0] = fx.SourceTexture?.Content;
            if (shader is BSLightingShaderProperty lsp) ReadPbr(lsp, m);
        }

        if (shape.HasAlphaProperty && nif.GetBlock(shape.AlphaPropertyRef) is { } alpha)
        {
            var blend = alpha.Flags.AlphaBlend;
            var test = alpha.Flags.AlphaTest;
            m.AlphaMode = test ? AlphaMode.Test : blend ? AlphaMode.Blend : AlphaMode.Opaque;
            if (blend && test) m.AlphaMode = AlphaMode.Test; // cutout with soft edges; test is the safe choice
            m.AlphaThreshold = alpha.Threshold / 255f;
        }
        return m;
    }

    /// <summary>
    /// Community Shaders marks a TruePBR material with the otherwise unused Menu_Screen flag (SLSF2
    /// bit 23) and reinterprets several fields; see its TruePBR.cpp and BSLightingShaderMaterialPBR.
    /// </summary>
    private static void ReadPbr(BSLightingShaderProperty lsp, MaterialInfo m)
    {
        var f2 = Convert.ToUInt32(lsp.ShaderFlags_SSPF2);
        if ((f2 & (1u << 23)) == 0) return;
        m.Pbr = true;
        m.PbrRoughnessScale = lsp.SpecularStrength;
        m.PbrSpecularLevel = lsp.Glossiness;

        // Fuzz: Soft_Lighting on a single-layer material without hair (Back_Lighting) shading. Its tint
        // and weight ride in the multilayer parallax fields, which only exist for that shader type.
        bool Flag(int bit) => (f2 & (1u << bit)) != 0;
        if (!Flag(24) && !Flag(27) && Flag(25) && lsp.IsTypeMultiLayerParallax)
        {
            m.FuzzColor = new Vector3(lsp.ParallaxInnerLayerThickness, lsp.ParallaxRefractionScale, lsp.ParallaxInnerLayerTextureScale.U);
            m.FuzzWeight = lsp.ParallaxInnerLayerTextureScale.V;
        }
    }
}
