using System.Numerics;
using System.Reflection;
using ItemNet.Render.Nif;
using NiflySharp;
using NiflySharp.Blocks;

namespace ItemNet.Cli;

public static partial class Commands
{
    /// <summary>
    /// nifdump <data-relative path>: each shape's shader (every simple property, by reflection), its
    /// texture slots, and tangent-frame statistics, to see how a mesh expects to be lit.
    /// </summary>
    public static int NifDump(CliOptions opts)
    {
        using var s = Session.Open(opts, verbose: false);
        var path = opts.Positional.FirstOrDefault() ?? throw new ArgumentException("path required");
        var data = s.Files.ReadAllBytes(path) ?? throw new FileNotFoundException(path);
        Console.WriteLine($"{path} [{s.Files.DescribeSource(path)}]");

        var nif = new NifFile();
        using (var ms = new MemoryStream(data)) nif.Load(ms, new NifFileLoadOptions());
        foreach (var root in nif.GetRootNodes())
            foreach (var ed in root.ExtraDataList.GetBlocks(nif))
                Console.WriteLine($"root extra: {ed?.GetType().Name} '{(ed as NiflySharp.Interfaces.INiNamed)?.Name?.String}' = '{(ed as NiStringExtraData)?.StringData?.String}'");
        foreach (var shape in nif.GetShapes())
        {
            Console.WriteLine($"* '{(shape as NiflySharp.Interfaces.INiNamed)?.Name?.String}' {shape.GetType().Name}");
            var chain = new List<string>();
            for (var p = nif.GetParentNode(shape); p is not null; p = nif.GetParentNode(p))
                chain.Add($"{(p as NiflySharp.Interfaces.INiNamed)?.Name?.String} ({p.GetType().Name}, t={p.Translation})");
            Console.WriteLine($"  parents: {string.Join(" <- ", chain)}");
            if (nif.GetShader(shape) is { } shader)
            {
                Console.WriteLine($"  shader {shader.GetType().Name}");
                foreach (var p in shader.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    object? v;
                    try { v = p.GetValue(shader); } catch { continue; }
                    if (v is null || v is string || v.GetType().IsPrimitive || v.GetType().IsEnum || v is Vector2 or Vector3 or Vector4)
                        Console.WriteLine($"    {p.Name} = {v}");
                    else if (v.GetType().IsValueType)
                        Console.WriteLine($"    {p.Name} = {string.Join(", ", v.GetType().GetFields().Select(f => $"{f.Name}:{f.GetValue(v)}"))}{(v.GetType().GetFields().Length == 0 ? v : "")}");
                }
                if (shader.HasTextureSet && nif.GetBlock(shader.TextureSetRef) is BSShaderTextureSet ts)
                    for (var i = 0; i < ts.Textures.Count; i++)
                        if (!string.IsNullOrWhiteSpace(ts.Textures[i]?.Content))
                            Console.WriteLine($"    tex[{i}] {ts.Textures[i].Content} [{s.Files.DescribeSource(ItemNet.Render.D3D.TextureCache.Normalize(ts.Textures[i].Content)) ?? "MISSING"}]");
            }
        }

        var model = NifModel.Load(path, data);
        foreach (var p in model.Parts)
        {
            if (p.Tangents is null || p.Bitangents is null) { Console.WriteLine($"- '{p.Name}': no tangents"); continue; }
            int right = 0, left = 0;
            float tLen = 0, bLen = 0, tn = 0, bn = 0, tb = 0;
            for (var i = 0; i < p.Positions.Length; i++)
            {
                var n = p.Normals[i]; var t = p.Tangents[i]; var b = p.Bitangents[i];
                tLen += t.Length(); bLen += b.Length();
                tn += MathF.Abs(Vector3.Dot(Vector3.Normalize(t), n)); bn += MathF.Abs(Vector3.Dot(Vector3.Normalize(b), n)); tb += MathF.Abs(Vector3.Dot(Vector3.Normalize(t), Vector3.Normalize(b)));
                if (Vector3.Dot(Vector3.Cross(t, b), n) > 0) right++; else left++;
            }
            var c = p.Positions.Length;
            Console.WriteLine($"- '{p.Name}' v={c}: |t| {tLen / c:F2} |b| {bLen / c:F2}  |t.n| {tn / c:F2} |b.n| {bn / c:F2} |t.b| {tb / c:F2}  (t x b).n>0: {right}, <0: {left}");
            // Which stored vector follows +U? Compare against the UV gradient of the first triangles.
            float tAlongU = 0, bAlongU = 0;
            for (var k = 0; k + 2 < p.Indices.Length && k < 3 * 2000; k += 3)
            {
                int i0 = p.Indices[k], i1 = p.Indices[k + 1], i2 = p.Indices[k + 2];
                var e1 = p.Positions[i1] - p.Positions[i0]; var e2 = p.Positions[i2] - p.Positions[i0];
                var d1 = p.Uvs[i1] - p.Uvs[i0]; var d2 = p.Uvs[i2] - p.Uvs[i0];
                var det = d1.X * d2.Y - d2.X * d1.Y;
                if (MathF.Abs(det) < 1e-9f) continue;
                var dPdu = (e1 * d2.Y - e2 * d1.Y) / det;
                if (dPdu.LengthSquared() < 1e-12f) continue;
                dPdu = Vector3.Normalize(dPdu);
                tAlongU += Vector3.Dot(Vector3.Normalize(p.Tangents[i0]), dPdu);
                bAlongU += Vector3.Dot(Vector3.Normalize(p.Bitangents[i0]), dPdu);
            }
            Console.WriteLine($"    sum dot with dP/du: tangent {tAlongU:F0}, bitangent {bAlongU:F0}");
        }
        return 0;
    }
}
