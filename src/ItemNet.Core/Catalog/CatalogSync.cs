using System.Buffers.Binary;
using ItemNet.Core.Plugins;
using ItemNet.Core.SkyrimNet;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace ItemNet.Core.Catalog;

public enum SyncAction
{
    /// <summary>Missing or out of date at the item's current runtime ID: (re)write it.</summary>
    Write,
    /// <summary>Already there with the same text.</summary>
    Current,
    /// <summary>SkyrimNet has a hand-edited description at that ID; left alone.</summary>
    HandEdited,
    /// <summary>The item's plugin isn't active, or the record is gone.</summary>
    Inactive,
}

public sealed record SyncItem(CatalogEntry Entry, uint? RuntimeId, SyncAction Action, string? Note = null);

/// <summary>An ItemNet row that now sits on a different item (or none), after a load-order change.</summary>
public sealed record StaleRow(OmniSightItemRow Row, string Reason, uint? MovedTo = null);

/// <summary>A hand-edited SkyrimNet row going back to its item, found by content_hash.</summary>
public sealed record MoveRow(OmniSightItemRow Row, uint NewRuntimeId, FormKey Item, string? ItemName);

public sealed class SyncPlan
{
    public required string DbPath { get; init; }
    public List<SyncItem> Items { get; } = [];
    public List<StaleRow> Stale { get; } = [];
    public List<MoveRow> Moves { get; } = [];
    /// <summary>Hand-edited rows that seem to have moved but can't be placed safely, and why.</summary>
    public List<string> Unplaced { get; } = [];
    public IEnumerable<SyncItem> ToWrite => Items.Where(i => i.Action == SyncAction.Write);
    public int Count(SyncAction a) => Items.Count(i => i.Action == a);
    public bool HasWork => Stale.Count > 0 || Moves.Count > 0 || Items.Any(i => i.Action == SyncAction.Write);
}

public sealed record SyncResult(int Written, int Removed, int Moved, int MissingImages);

/// <summary>
/// Puts the approved catalog into a SkyrimNet playthrough DB at each item's current runtime ID.
/// The catalog is keyed by FormKey, so it survives load-order changes; SkyrimNet keys rows by the
/// runtime FormID, which moves whenever a plugin is added, removed or reordered ahead of the item.
/// </summary>
public static class CatalogSync
{
    public static SyncPlan Plan(GameData game, CatalogStore catalog, string dbPath)
    {
        List<OmniSightItemRow> rows;
        using (var db = new OmniSightDb(dbPath)) rows = db.ReadItems();
        var byId = rows.GroupBy(r => (r.RuntimeFormId, r.Gender)).ToDictionary(g => g.Key, g => g.First());

        var plan = new SyncPlan { DbPath = dbPath };
        var approved = catalog.All().Where(e => e.Status == EntryStatus.Approved).ToList();
        var catalogKeys = approved.Select(e => (e.Key, e.Gender)).ToHashSet();
        var targets = new HashSet<(uint, ItemGender)>();
        foreach (var e in approved)
            if (game.ToRuntimeFormId(e.Key) is { } id) targets.Add((id, e.Gender));

        // ItemNet rows whose ID now belongs to another item. Rows the sync is about to rewrite are
        // fine; the rest describe the wrong item and are removed so SkyrimNet describes it afresh.
        var lastWrittenAt = catalog.Syncs(dbPath)
            .GroupBy(s => (s.RuntimeId, s.Gender))
            .ToDictionary(g => g.Key, g => g.Select(s => s.Key).ToList());
        foreach (var row in rows.Where(r => r.DescriptionModel?.StartsWith("ItemNet", StringComparison.Ordinal) == true))
        {
            if (targets.Contains((row.RuntimeFormId, row.Gender))) continue;
            var key = game.FromRuntimeFormId(row.RuntimeFormId);
            IArmorGetter? now = null;
            if (key is { } k) game.LinkCache.TryResolve(k, out now);
            var nowName = now?.Name?.String;
            lastWrittenAt.TryGetValue((row.RuntimeFormId, row.Gender), out var writtenFor);
            var stale = now is null
                || (writtenFor is not null && !writtenFor.Contains(now.FormKey))
                || !string.Equals(row.SubjectName, nowName, StringComparison.Ordinal);
            if (!stale) continue;

            // Say where the item went, so a long list reads as "moved", not "missing".
            var item = writtenFor?.FirstOrDefault(k => catalogKeys.Contains((k, row.Gender)));
            if (item is null)
            {
                var byName = approved.Where(e => e.Gender == row.Gender && e.GameName == row.SubjectName).Take(2).ToList();
                if (byName.Count == 1) item = byName[0].Key;
            }
            var movedTo = item is { } it ? game.ToRuntimeFormId(it) : null;
            var holds = now is null ? "this ID now holds no armour" : $"this ID now holds \"{nowName}\"";
            var reason = movedTo is { } m
                ? $"moved to {OmniSightDb.MakeIdentifier(m, row.Gender)}, rewritten there; {holds}"
                : item is not null ? $"its plugin is no longer active; {holds}"
                : $"no catalog entry to rewrite it from; {holds}";
            plan.Stale.Add(new StaleRow(row, reason, movedTo));
        }
        var staleIds = plan.Stale.Select(s => (s.Row.RuntimeFormId, s.Row.Gender)).ToHashSet();

        PlanMoves(game, rows, byId, catalogKeys, staleIds, plan);
        var movingAway = plan.Moves.Select(m => (m.Row.RuntimeFormId, m.Row.Gender)).ToHashSet();

        foreach (var e in approved)
        {
            var rt = game.ToRuntimeFormId(e.Key);
            if (rt is null || !game.LinkCache.TryResolve<IArmorGetter>(e.Key, out _))
            {
                plan.Items.Add(new SyncItem(e, null, SyncAction.Inactive, "plugin not active or record gone"));
                continue;
            }
            // A hand-edited row that is about to move back to its own item doesn't count here.
            var row = movingAway.Contains((rt.Value, e.Gender)) ? null : byId.GetValueOrDefault((rt.Value, e.Gender));
            if (row is { DescriptionModel: "user-edited" })
                plan.Items.Add(new SyncItem(e, rt, SyncAction.HandEdited, $"SkyrimNet has \"{row.SuggestedName}\" there, hand-edited"));
            else if (row is { Locked: true } && row.DescriptionModel == e.Model && row.SuggestedName == e.SuggestedName && row.Description == e.Description)
                plan.Items.Add(new SyncItem(e, rt, SyncAction.Current));
            else
                plan.Items.Add(new SyncItem(e, rt, SyncAction.Write));
        }
        return plan;
    }

    /// <summary>
    /// Hand-edited rows are locked, so SkyrimNet never fixes them when the load order moves their
    /// ID. Their content_hash still names the item they were written for: move each one back when
    /// exactly one item has that hash and its slot is free (empty, an unlocked automatic row, a
    /// stale ItemNet row, or a row that is itself moving away).
    /// </summary>
    private static void PlanMoves(GameData game, List<OmniSightItemRow> rows, Dictionary<(uint, ItemGender), OmniSightItemRow> byId,
        HashSet<(FormKey, ItemGender)> catalogKeys, HashSet<(uint, ItemGender)> staleIds, SyncPlan plan)
    {
        var edited = rows.Where(r => r.DescriptionModel == "user-edited" && !string.IsNullOrEmpty(r.ContentHash)).ToList();
        string? HashAt(uint id, ItemGender g) =>
            game.FromRuntimeFormId(id) is { } k && game.LinkCache.TryResolve<IArmorGetter>(k, out var a)
                ? ContentHash.Compute(game, a, g == ItemGender.Female) : null;
        var misplaced = edited.Where(r => !string.Equals(HashAt(r.RuntimeFormId, r.Gender), r.ContentHash, StringComparison.OrdinalIgnoreCase)).ToList();
        if (misplaced.Count == 0) return;

        // Every item's hash, both genders, built only when something has moved.
        var byHash = new Dictionary<(string, ItemGender), List<IArmorGetter>>();
        foreach (var a in game.LoadOrder.PriorityOrder.Armor().WinningOverrides())
            foreach (var g in new[] { ItemGender.Female, ItemGender.Male })
                if (ContentHash.Compute(game, a, g == ItemGender.Female) is { } h)
                {
                    var key = (h.ToUpperInvariant(), g);
                    if (!byHash.TryGetValue(key, out var list)) byHash[key] = list = [];
                    list.Add(a);
                }

        var candidates = new List<MoveRow>();
        foreach (var r in misplaced)
        {
            var label = $"{OmniSightDb.MakeIdentifier(r.RuntimeFormId, r.Gender)} \"{r.SuggestedName}\"";
            if (!byHash.TryGetValue((r.ContentHash!.ToUpperInvariant(), r.Gender), out var matches))
            { plan.Unplaced.Add($"{label}: no item matches its content hash"); continue; }
            if (matches.Count > 1)
            { plan.Unplaced.Add($"{label}: {matches.Count} items share its content hash"); continue; }
            var item = matches[0];
            if (catalogKeys.Contains((item.FormKey, r.Gender)))
            { plan.Unplaced.Add($"{label}: ItemNet's catalog has its own description for {item.Name?.String}"); continue; }
            if (game.ToRuntimeFormId(item.FormKey) is not { } newId) continue;
            candidates.Add(new MoveRow(r, newId, item.FormKey, item.Name?.String));
        }

        var leaving = candidates.Select(c => (c.Row.RuntimeFormId, c.Row.Gender)).ToHashSet();
        foreach (var group in candidates.GroupBy(c => (c.NewRuntimeId, c.Row.Gender)))
        {
            var label = $"{group.Count()} hand-edited rows for {group.First().ItemName}";
            if (group.Count() > 1) { plan.Unplaced.Add($"{label} all want {OmniSightDb.MakeIdentifier(group.Key.NewRuntimeId, group.Key.Gender)}"); continue; }
            var move = group.First();
            var there = byId.GetValueOrDefault(group.Key);
            var free = there is null || !there.Locked || leaving.Contains(group.Key) || staleIds.Contains(group.Key);
            if (free) plan.Moves.Add(move);
            else plan.Unplaced.Add($"{OmniSightDb.MakeIdentifier(move.Row.RuntimeFormId, move.Row.Gender)} \"{move.Row.SuggestedName}\": " +
                                   $"{move.ItemName}'s slot already has a locked row (\"{there!.SuggestedName}\")");
        }
    }

    /// <summary>
    /// Removes the stale rows, then writes each entry with its catalog images (renamed for the new
    /// ID) and records where it went. The caller backs the DB up first.
    /// </summary>
    public static SyncResult Apply(SyncPlan plan, GameData game, CatalogStore catalog, SkyrimNetPaths paths, IProgress<string>? progress = null) =>
        Apply(plan, game, catalog, paths.ImageWriteDirectory, paths.GameVisibleImagePath, progress);

    /// <param name="imageWriteDirectory">Where image files go on disk.</param>
    /// <param name="gameVisiblePath">The path SkyrimNet should record for an image file name.</param>
    public static SyncResult Apply(SyncPlan plan, GameData game, CatalogStore catalog, string imageWriteDirectory,
        Func<string, string> gameVisiblePath, IProgress<string>? progress = null)
    {
        int written = 0, removed = 0, moved = 0, missingImages = 0;
        using var db = new OmniSightDb(plan.DbPath, readOnly: false);
        foreach (var s in plan.Stale)
            if (db.Delete(s.Row.RuntimeFormId, s.Row.Gender)) removed++;

        // Hand-edited rows back to their items: clear what's left in each destination (unlocked
        // automatic rows), park every moving row under a temporary name so two rows can swap, then
        // give each its new ID.
        var leaving = plan.Moves.Select(m => OmniSightDb.MakeIdentifier(m.Row.RuntimeFormId, m.Row.Gender)).ToHashSet();
        foreach (var m in plan.Moves)
        {
            var dest = OmniSightDb.MakeIdentifier(m.NewRuntimeId, m.Row.Gender);
            if (!leaving.Contains(dest) && db.GetItem(m.NewRuntimeId, m.Row.Gender) is { Locked: false })
                db.Delete(m.NewRuntimeId, m.Row.Gender);
        }
        foreach (var m in plan.Moves)
            db.Rename(OmniSightDb.MakeIdentifier(m.Row.RuntimeFormId, m.Row.Gender), "itemnet-moving-" + OmniSightDb.MakeIdentifier(m.Row.RuntimeFormId, m.Row.Gender));
        foreach (var m in plan.Moves)
            if (db.Rename("itemnet-moving-" + OmniSightDb.MakeIdentifier(m.Row.RuntimeFormId, m.Row.Gender), OmniSightDb.MakeIdentifier(m.NewRuntimeId, m.Row.Gender)))
                moved++;

        Directory.CreateDirectory(imageWriteDirectory);
        var toWrite = plan.ToWrite.ToList();
        foreach (var item in toWrite)
        {
            var e = item.Entry;
            var rt = item.RuntimeId!.Value;
            var armo = game.LinkCache.Resolve<IArmorGetter>(e.Key);
            var visible = new List<string>();
            int width = 0, height = 0;
            long bytes = 0;
            for (var i = 0; i < e.Images.Count; i++)
            {
                if (!File.Exists(e.Images[i])) { missingImages++; continue; }
                var png = File.ReadAllBytes(e.Images[i]);
                var name = SkyrimNetPaths.ImageFileName(rt, e.Gender, i);
                File.WriteAllBytes(Path.Combine(imageWriteDirectory, name), png);
                visible.Add(gameVisiblePath(name));
                bytes += png.Length;
                if (width == 0 && png.Length >= 24)
                {
                    width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
                    height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
                }
            }
            db.Upsert(new OmniSightWrite(rt, e.Gender, armo.Name?.String ?? e.GameName, e.SuggestedName, e.Description, e.Model,
                visible, width, height, bytes, ContentHash.Compute(game, armo, e.Gender == ItemGender.Female), ReplaceContentHash: true));
            catalog.RecordSync(e.Key, e.Gender, plan.DbPath, rt);
            if (++written % 25 == 0) progress?.Report($"Written {written} of {toWrite.Count}…");
        }
        return new SyncResult(written, removed, moved, missingImages);
    }
}
