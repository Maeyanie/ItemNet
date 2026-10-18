using ItemNet.Core.Items;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace ItemNet.Cli;

public static partial class Commands
{
    /// <summary>
    /// items [--out file]: the item list as the GUI builds it, one tab-separated line per item, sorted
    /// by FormKey so two sources can be diffed. With --out, errors go to the file too, since a run
    /// launched from MO2 has no console to read.
    /// </summary>
    public static int Items(CliOptions opts)
    {
        var lines = new List<string>();
        try
        {
            using var s = Session.Open(opts, verbose: false);
            var items = ItemIndex.Build(s.Game);
            lines.Add($"# {s.Source.Description}: {items.Count} items from {s.Game.Plugins.Count} plugins");
            lines.AddRange(s.Game.MissingPlugins.Select(m => $"# missing plugin: {m}"));
            lines.AddRange(items.OrderBy(i => i.FormKeyString, StringComparer.Ordinal).Select(i =>
                $"{i.FormKeyString}\t{i.EditorId}\t{i.Name}\t{(i.HasFemaleModel ? "F" : "")}{(i.HasMaleModel ? "M" : "")}"));

            // --why <FormKey,...>: each override of those records, origin first, with the file it was
            // read from and the fields that decide whether the item is listed.
            foreach (var key in (opts.Get("why") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                lines.Add($"# why {key}:");
                foreach (var c in s.Game.LinkCache.ResolveAllContexts<IArmor, IArmorGetter>(FormKey.Factory(key)).Reverse())
                {
                    var a = c.Record;
                    var arma = a.Armature.Select(x => x.TryResolve(s.Game.LinkCache)).Where(x => x is not null).ToList();
                    lines.Add($"#   {s.Game.GetPlugin(c.ModKey)?.Path}: nonplayable={a.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable)}" +
                              $" name='{a.Name?.String}' addons={a.Armature.Count}/{arma.Count} resolved" +
                              $" models={arma.Count(x => !string.IsNullOrEmpty(x!.WorldModel?.Female?.File.GivenPath) || !string.IsNullOrEmpty(x.WorldModel?.Male?.File.GivenPath))}");
                }
            }
            return 0;
        }
        catch (Exception e) when (opts.Get("out") is not null)
        {
            lines.Add($"# error: {e}");
            return 1;
        }
        finally
        {
            if (opts.Get("out") is { } path) File.WriteAllLines(path, lines);
            else lines.ForEach(Console.WriteLine);
        }
    }
}
