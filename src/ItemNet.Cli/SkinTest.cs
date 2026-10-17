using System.Numerics;
using ItemNet.Render.Nif;

namespace ItemNet.Cli;

public static partial class Commands
{
    /// <summary>
    /// In bind pose every influence of a vertex should put it in the same place. Measures the
    /// average spread between influences for each bone source and matrix convention.
    /// </summary>
    public static int SkinTest(CliOptions opts)
    {
        using var s = Session.Open(opts, verbose: false);
        var nifPath = opts.Positional[0];
        var skelPath = opts.Get("skeleton", @"meshes\actors\character\character assets female\skeleton_female.nif");
        var nif = NifModel.Load(nifPath, s.Files.ReadAllBytes(nifPath) ?? throw new FileNotFoundException(nifPath));
        var skel = NifModel.Load(skelPath, s.Files.ReadAllBytes(skelPath)!).NodeTransforms;

        foreach (var part in nif.Parts.Where(p => p.IsSkinned))
        {
            Console.WriteLine($"{part.Name}: {part.Positions.Length} verts, {part.BoneNames!.Length} bones, missing in skeleton: {part.BoneNames.Count(b => !skel.ContainsKey(b))}");
            foreach (var (label, src) in new[] { ("skeleton", skel), ("nif", nif.NodeTransforms) })
                foreach (var transpose in new[] { false, true })
                {
                    Matrix4x4 Fix(Matrix4x4 m) => transpose ? TransposeRotation(m) : m;
                    double spread = 0; int n = 0;
                    Vector3 sum = default;
                    for (var v = 0; v < part.Positions.Length; v++)
                    {
                        var pts = new List<Vector3>();
                        for (var k = 0; k < 4; k++)
                        {
                            if (part.BoneWeights![v * 4 + k] <= 0) continue;
                            var b = part.BoneIndices![v * 4 + k];
                            var world = src.TryGetValue(part.BoneNames[b], out var w) ? w : Matrix4x4.Identity;
                            pts.Add(Vector3.Transform(part.Positions[v], Fix(part.SkinToBone![b]) * Fix(world)));
                        }
                        if (pts.Count == 0) continue;
                        var c = pts.Aggregate(Vector3.Zero, (a, p) => a + p) / pts.Count;
                        sum += c;
                        spread += pts.Max(p => (p - c).Length());
                        n++;
                    }
                    Console.WriteLine($"   {label,-8} transpose={transpose,-5}: mean spread {spread / Math.Max(1, n):F3}, centroid {sum / Math.Max(1, n)}");
                }
        }
        return 0;
    }

    private static Matrix4x4 TransposeRotation(Matrix4x4 m)
    {
        var t = new Vector3(m.M41, m.M42, m.M43);
        var r = Matrix4x4.Transpose(m with { M41 = 0, M42 = 0, M43 = 0 });
        r.M41 = t.X; r.M42 = t.Y; r.M43 = t.Z;
        r.M14 = 0; r.M24 = 0; r.M34 = 0; r.M44 = 1;
        return r;
    }
}
