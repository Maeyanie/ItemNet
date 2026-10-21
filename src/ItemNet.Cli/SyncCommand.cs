using ItemNet.Core.Catalog;
using ItemNet.Core.SkyrimNet;

namespace ItemNet.Cli;

public static partial class Commands
{
    /// <summary>
    /// sync --db &lt;path&gt; --catalog &lt;dir&gt; [--apply --images &lt;dir&gt;]: what a catalog sync would
    /// do to a playthrough DB, and with --apply, do it. Meant for copies; refuses MO2 paths, and
    /// images go to --images rather than overwrite.
    /// </summary>
    public static int Sync(CliOptions opts)
    {
        var dbPath = opts.Get("db") ?? throw new ArgumentException("--db required");
        var catalogDir = opts.Get("catalog") ?? throw new ArgumentException("--catalog required");
        var apply = opts.Has("apply");
        if (apply && dbPath.Contains(@"\MO2Data\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Refusing to --apply to a DB inside MO2; copy it first.");

        using var s = Session.Open(opts, verbose: false);
        using var catalog = new CatalogStore(catalogDir);
        var plan = CatalogSync.Plan(s.Game, catalog, dbPath);
        foreach (var a in Enum.GetValues<SyncAction>()) Console.WriteLine($"{a,-11} {plan.Count(a)}");
        foreach (var i in plan.Items.Where(i => i.Action != SyncAction.Current))
            Console.WriteLine($"  {i.Action,-10} {i.Entry.Key} {i.Entry.Gender} -> {(i.RuntimeId is { } r ? r.ToString("X8") : "-")} \"{i.Entry.SuggestedName}\" {i.Note}");
        Console.WriteLine($"Stale      {plan.Stale.Count}");
        foreach (var st in plan.Stale)
            Console.WriteLine($"  {OmniSightDb.MakeIdentifier(st.Row.RuntimeFormId, st.Row.Gender)} \"{st.Row.SuggestedName}\": {st.Reason}");
        Console.WriteLine($"Moves      {plan.Moves.Count}");
        foreach (var m in plan.Moves)
            Console.WriteLine($"  {OmniSightDb.MakeIdentifier(m.Row.RuntimeFormId, m.Row.Gender)} \"{m.Row.SuggestedName}\" -> {m.NewRuntimeId:X8} {m.Item} \"{m.ItemName}\"");
        foreach (var u in plan.Unplaced) Console.WriteLine("  unplaced: " + u);
        if (!apply) return 0;

        var images = opts.Get("images") ?? throw new ArgumentException("--images required with --apply");
        var result = CatalogSync.Apply(plan, s.Game, catalog, images, name => Path.Combine(images, name));
        Console.WriteLine($"Applied: {result.Written} written, {result.Removed} removed, {result.Moved} moved, {result.MissingImages} images missing");
        return 0;
    }
}
