using ItemNet.Core.SkyrimNet;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace ItemNet.Cli;

public static partial class Commands
{
    public static int LoadOrder(CliOptions opts)
    {
        using var s = Session.Open(opts);

        // MO2's loadorder.txt lists every plugin it knows of; keep the active ones and compare order.
        var ours = s.Game.Plugins.Select(p => p.ModKey.FileName.String).ToList();
        var activeSet = ours.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (s.Source.Profile is not { } profile) { Console.WriteLine("(no MO2 loadorder.txt to compare against)"); return 0; }
        var mo2 = File.ReadLines(profile.LoadOrderTxt)
            .Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#' && activeSet.Contains(l)).ToList();
        var mismatch = ours.Zip(mo2).Select((pair, i) => (pair, i))
            .FirstOrDefault(x => !string.Equals(x.pair.First, x.pair.Second, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine(mismatch.pair == default && ours.Count == mo2.Count
            ? $"Order matches MO2 loadorder.txt ({ours.Count} active)."
            : $"Order differs from loadorder.txt at #{mismatch.i}: ours={mismatch.pair.First} mo2={mismatch.pair.Second} (counts {ours.Count}/{mo2.Count})");

        foreach (var p in s.Game.Plugins.Where(p => opts.Has("all") || p.ModKey.FileName.String.StartsWith("cc", StringComparison.OrdinalIgnoreCase) || p.RuntimeIndex < 8))
            Console.WriteLine($"  [{p.RuntimePrefix}] {p.ModKey.FileName}{(p.IsMaster ? " (master)" : "")}");
        return 0;
    }

    public static int Parity(CliOptions opts)
    {
        var dbPath = opts.Get("db") ?? throw new ArgumentException("--db required");
        using var s = Session.Open(opts);
        using var db = new OmniSightDb(dbPath);
        Console.WriteLine($"DB schema version {db.SchemaVersion()}");

        // Rows are keyed by the runtime FormID at capture time, so older rows go stale when the
        // load order changes. Report per capture date; recent rows should all match.
        var since = opts.Get("since", "");
        var verbose = opts.Has("verbose");
        var perDate = new SortedDictionary<string, (int ok, int bad)>();
        void Tally(OmniSightItemRow r, bool good)
        {
            var d = r.CaptureTimeLocal?[..10] ?? "?";
            var t = perDate.GetValueOrDefault(d);
            perDate[d] = good ? (t.ok + 1, t.bad) : (t.ok, t.bad + 1);
        }

        int ok = 0, renamed = 0, unresolved = 0, notArmorOrWeapon = 0;
        foreach (var row in db.ReadItems().OrderBy(r => r.RuntimeFormId))
        {
            var recent = string.CompareOrdinal(row.CaptureTimeLocal ?? "", since) >= 0;
            var key = s.Game.FromRuntimeFormId(row.RuntimeFormId);
            if (key is null || !s.Game.LinkCache.TryResolve(key.Value, out var rec, ResolveTarget.Winner))
            {
                unresolved++; Tally(row, false);
                if (recent || verbose) Console.WriteLine($"  NO RECORD  {row.RuntimeFormId:X8} -> {key} {row.SubjectName} [{row.CaptureTimeLocal}]");
                continue;
            }
            var name = rec switch
            {
                IArmorGetter a => a.Name?.String,
                IWeaponGetter w => w.Name?.String,
                INamedGetter n => n.Name,
                _ => null,
            };
            if (rec is not (IArmorGetter or IWeaponGetter)) notArmorOrWeapon++;
            var same = string.Equals(name, row.SubjectName, StringComparison.Ordinal);
            Tally(row, same);
            if (same) ok++;
            else
            {
                renamed++;
                if (recent || verbose) Console.WriteLine($"  NAME DIFF  {row.RuntimeFormId:X8} -> {key} ours='{name}' skyrimnet='{row.SubjectName}' ({rec.GetType().Name}) [{row.CaptureTimeLocal}]");
            }
        }
        foreach (var (d, t) in perDate) Console.WriteLine($"  {d}: {t.ok,3} match, {t.bad,3} stale");
        Console.WriteLine($"Matched {ok}, name differs {renamed}, unresolved {unresolved}; non-ARMO/WEAP records {notArmorOrWeapon}");
        return 0;
    }
}
