using System.Text;
using ItemNet.Core.SkyrimNet;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace ItemNet.Cli;

public static partial class Commands
{
    /// <summary>
    /// Tries candidate inputs for SkyrimNet's content hash against existing rows.
    /// Known from disassembly: FNV-1a 64 over (prefix + "|" + tex1 + "|" + tex2 ...), "%016llX",
    /// where prefix = [EditorID + "|"] + GetModelPath(item, female) and the texture list is the
    /// sorted, de-duplicated paths of the item's alternate texture sets.
    /// </summary>
    public static int HashTest(CliOptions opts)
    {
        var dbPath = opts.Get("db") ?? throw new ArgumentException("--db required");
        using var s = Session.Open(opts, verbose: false);
        using var db = new OmniSightDb(dbPath);

        var tally = new Dictionary<string, int>();
        int tested = 0;
        var misses = new List<string>();
        foreach (var row in db.ReadItems())
        {
            if (row.ContentHash is null) continue;
            var key = s.Game.FromRuntimeFormId(row.RuntimeFormId);
            if (key is null || !s.Game.LinkCache.TryResolve<ISkyrimMajorRecordGetter>(key.Value, out var rec)) continue;
            if (rec is not (IArmorGetter or IWeaponGetter)) continue;
            var name = rec is IArmorGetter a0 ? a0.Name?.String : ((IWeaponGetter)rec).Name?.String;
            if (!string.Equals(name, row.SubjectName, StringComparison.Ordinal)) continue; // stale row
            tested++;

            var female = row.Gender == ItemGender.Female;
            if (opts.Has("rule"))
            {
                // Single prediction: what ItemNet would write for a new row.
                var predicted = ItemNet.Core.SkyrimNet.ContentHash.Compute(s.Game, rec, female);
                var good = predicted == row.ContentHash;
                tally[good ? "rule matches" : "rule misses"] = tally.GetValueOrDefault(good ? "rule matches" : "rule misses") + 1;
                if (!good) misses.Add($"{row.RuntimeFormId:X8}_{row.Gender} {rec.EditorID} [{row.CaptureTimeLocal}] want {row.ContentHash} got {predicted}");
                continue;
            }
            var hit = false;
            foreach (var (label, input) in Candidates(s, rec, female))
            {
                if (Fnv(input) != row.ContentHash) continue;
                tally[label] = tally.GetValueOrDefault(label) + 1;
                // "edid|model" has one separator; more means texture paths were part of the match.
                if (input.Count(c => c == '|') > 1) tally["  (of those, with textures in the input)"] = tally.GetValueOrDefault("  (of those, with textures in the input)") + 1;
                hit = true;
                if (opts.Has("chains"))
                {
                    // Which override's EditorID did the game keep? Show the chain, origin first.
                    var chain = s.Game.LinkCache.ResolveAllContexts<ISkyrimMajorRecord, ISkyrimMajorRecordGetter>(rec.FormKey).Reverse()
                        .Select(c => { var p = s.Game.GetPlugin(c.ModKey)!; return $"{c.ModKey.FileName}{(p.IsMaster ? "[M]" : "")}{(p.IsLight ? "[L]" : "")}={c.Record.EditorID}"; });
                    var edids = s.Game.LinkCache.ResolveAllContexts<ISkyrimMajorRecord, ISkyrimMajorRecordGetter>(rec.FormKey).Select(c => c.Record.EditorID).Distinct().Count();
                    if (edids > 1) Console.WriteLine($"  {label,-60} {string.Join("  ->  ", chain)}");
                }
                break;
            }
            if (!hit && misses.Count < 15)
                misses.Add($"{row.RuntimeFormId:X8}_{row.Gender} {rec.EditorID} want {row.ContentHash}; e.g. '{Candidates(s, rec, female).First().input}'");
        }

        Console.WriteLine($"Tested {tested} current rows");
        foreach (var (label, n) in tally.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {n,4}  {label}");
        Console.WriteLine($"  {tested - tally.Where(kv => !kv.Key.StartsWith(' ')).Sum(kv => kv.Value),4}  no candidate matched");
        foreach (var m in misses.Take(opts.Has("all") ? int.MaxValue : 15)) Console.WriteLine("    miss " + m);
        return 0;
    }

    private static string Fnv(string s)
    {
        ulong h = 0xCBF29CE484222325;
        foreach (var b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 0x100000001B3; }
        return h.ToString("X16");
    }

    private static IEnumerable<(string label, string input)> Candidates(Session s, ISkyrimMajorRecordGetter rec, bool female)
    {
        var models = new List<(string label, string? path)>();
        var texSets = new List<IModelGetter?>();
        if (rec is IArmorGetter armo)
        {
            var addons = armo.Armature.Select(l => l.TryResolve(s.Game.LinkCache)).Where(x => x is not null).ToList();
            var first = addons.FirstOrDefault();
            string? G(IGenderedItemGetter<IModelGetter?>? g, bool f) => (f ? g?.Female : g?.Male)?.File.GivenPath;
            string? W(bool f) => (f ? armo.WorldModel?.Female : armo.WorldModel?.Male)?.Model?.File.GivenPath;
            models.Add(("arma0[g]??arma0[other]", G(first?.WorldModel, female) ?? G(first?.WorldModel, !female)));
            models.Add(("anyArma[g]??anyArma[other]", addons.Select(a => G(a!.WorldModel, female)).FirstOrDefault(p => !string.IsNullOrEmpty(p))
                ?? addons.Select(a => G(a!.WorldModel, !female)).FirstOrDefault(p => !string.IsNullOrEmpty(p))));
            models.Add(("armoWorld[g]??other", W(female) ?? W(!female)));
            texSets.Add(female ? armo.WorldModel?.Female?.Model : armo.WorldModel?.Male?.Model);
            foreach (var a in addons) { texSets.Add(a!.WorldModel?.Female); texSets.Add(a.WorldModel?.Male); }
        }
        else if (rec is IWeaponGetter weap)
        {
            models.Add(("weap.Model", weap.Model?.File.GivenPath));
            texSets.Add(weap.Model);
        }

        string Suffix(IEnumerable<IModelGetter?> sets) => string.Concat(sets
            .Where(m => m?.AlternateTextures is not null)
            .SelectMany(m => m!.AlternateTextures!)
            .Select(at => at.NewTexture.TryResolve(s.Game.LinkCache))
            .Where(t => t is not null)
            .SelectMany(t => new[] { t!.Diffuse, t.NormalOrGloss, t.EnvironmentMaskOrSubsurfaceTint, t.GlowOrDetailMap,
                                     t.Height, t.Environment, t.Multilayer, t.BacklightMaskOrSpecular })
            .Select(p => p?.GivenPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(t => "|" + t));
        var texSuffix = Suffix(texSets);
        // Variants: only the row's own gender, or only the first addon's model of that gender.
        var genderSets = new List<IModelGetter?>();
        string? arma0Gender = null;
        if (rec is IArmorGetter ag)
        {
            genderSets.Add(female ? ag.WorldModel?.Female?.Model : ag.WorldModel?.Male?.Model);
            var addons = ag.Armature.Select(l => l.TryResolve(s.Game.LinkCache)).Where(x => x is not null).ToList();
            foreach (var a in addons) genderSets.Add(female ? a!.WorldModel?.Female : a!.WorldModel?.Male);
            var first = addons.FirstOrDefault();
            arma0Gender = Suffix([female ? first?.WorldModel?.Female ?? first?.WorldModel?.Male : first?.WorldModel?.Male ?? first?.WorldModel?.Female]);
        }
        var texGender = Suffix(genderSets);
        // The same texture sets as their original records, before any plugin overrode them.
        var texOrigin = string.Concat(texSets
            .Where(m => m?.AlternateTextures is not null)
            .SelectMany(m => m!.AlternateTextures!)
            .Select(at => s.Game.LinkCache.ResolveAllContexts<ITextureSet, ITextureSetGetter>(at.NewTexture.FormKey).LastOrDefault()?.Record)
            .Where(t => t is not null)
            .SelectMany(t => new[] { t!.Diffuse, t.NormalOrGloss, t.EnvironmentMaskOrSubsurfaceTint, t.GlowOrDetailMap,
                                     t.Height, t.Environment, t.Multilayer, t.BacklightMaskOrSpecular })
            .Select(p => p?.GivenPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(t => "|" + t));

        // EditorIDs along the override chain, winner first.
        var contexts = s.Game.LinkCache.ResolveAllContexts<ISkyrimMajorRecord, ISkyrimMajorRecordGetter>(rec.FormKey).ToList();
        var edids = contexts.Select((c, i) => (label: i == 0 ? "edid(winner)|" : i == contexts.Count - 1 ? "edid(origin)|" : $"edid(ovr{i}:{c.ModKey.Name})|", value: c.Record.EditorID))
            .Where(e => e.value is not null).ToList();

        foreach (var (label, path) in models)
        {
            if (string.IsNullOrEmpty(path)) continue;
            foreach (var (pl, prefix) in edids.Select(e => (e.label, e.value + "|")).Append(("", "")))
            {
                yield return ($"{pl}{label}+tex", prefix + path + texSuffix);
                if (texGender != texSuffix) yield return ($"{pl}{label}+texGender", prefix + path + texGender);
                if (texOrigin != texSuffix) yield return ($"{pl}{label}+texOrigin", prefix + path + texOrigin);
                if (arma0Gender is not null && arma0Gender != texSuffix && arma0Gender != texGender)
                    yield return ($"{pl}{label}+texArma0Gender", prefix + path + arma0Gender);
                yield return ($"{pl}{label}", prefix + path);
                yield return ($"{pl}{label} lower", (prefix + path).ToLowerInvariant());
            }
        }
    }
}
