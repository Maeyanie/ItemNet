using ItemNet.Core.Items;
using ItemNet.Core.SkyrimNet;
using ItemNet.Render.Scene;
using ItemNet.Vision;

namespace ItemNet.App.Services;

public enum BatchGender { Female, Male, Both }

public sealed record BatchJob(ItemEntry Item, ItemGender Gender)
{
    /// <summary>Un-described items that look identical (same meshes and textures): they get the same result.</summary>
    public IReadOnlyList<ItemEntry> Copies { get; init; } = [];

    public override string ToString() => $"{Item.Name} ({(Gender == ItemGender.Female ? "F" : "M")})";
}

public enum BatchOutcome
{
    /// <summary>Saved to the catalog as a draft, for review.</summary>
    Drafted,
    /// <summary>Written to SkyrimNet (and the catalog, approved).</summary>
    Saved,
    /// <summary>Writing straight to SkyrimNet, but its row was hand-edited, so this became a draft.</summary>
    KeptAsDraft,
    Failed,
}

/// <summary>Progress from a running batch. <see cref="Outcome"/> is null for in-flight notices (retries).</summary>
public sealed record BatchEvent(BatchJob Job, BatchOutcome? Outcome, string Message, double? Cost = null, int Copied = 0);

public sealed class BatchSettings
{
    public required ProviderConfig Provider { get; init; }
    public required VisionModelConfig Model { get; init; }
    public required string PromptTemplate { get; init; }
    public required SceneOptions Scene { get; init; }
    public required int RenderSize { get; init; }
    public int Concurrency { get; init; } = 3;
    /// <summary>Write each result straight to SkyrimNet instead of leaving a draft to review.</summary>
    public bool WriteToSkyrimNet { get; init; }
}

/// <summary>
/// Renders and describes a list of items. Renders run one at a time (one D3D context); up to
/// <see cref="BatchSettings.Concurrency"/> describe requests run at once, each with the model's
/// retries. Results are saved through <c>onUi</c>, on the UI thread, because the catalog and the
/// workspace's caches aren't thread-safe.
/// </summary>
public static class BatchRunner
{
    public static Task RunAsync(Workspace ws, IReadOnlyList<BatchJob> jobs, BatchSettings s,
        Func<Action, Task> onUi, Action<BatchEvent> report, CancellationToken ct)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, s.Concurrency), CancellationToken = ct };
        var renderSettings = Workspace.DescribeRenderSettings(s.Scene, s.RenderSize);
        var textured = s.Scene.ShowMannequin && s.Scene.Mannequin == MannequinStyle.Textured;

        return Parallel.ForEachAsync(jobs, options, async (job, token) =>
        {
            try
            {
                var (views, _) = await ws.RenderAsync(job.Item, job.Gender, s.Scene, s.RenderSize, token);
                var prompt = Vision.PromptTemplate.Render(s.PromptTemplate, Workspace.Facts(job.Item, job.Gender, textured, views));
                var result = await ws.DescribeAsync(s.Provider, s.Model, prompt, views, token, n =>
                    report(new BatchEvent(job, null, $"attempt {n.Attempt} of {n.MaxAttempts} failed, retrying in {n.Delay.TotalSeconds:F0}s: {n.Reason}")));
                token.ThrowIfCancellationRequested();

                var outcome = BatchOutcome.Drafted;
                var copied = 0;
                await onUi(() =>
                {
                    outcome = SaveOne(ws, job.Item, job.Gender, result, s, views, renderSettings);
                    // Identical items share the views too: same meshes and textures render the same.
                    // Re-checked here, as one may have been described since the batch started.
                    foreach (var copy in job.Copies)
                        if (IsUndescribed(ws, copy, job.Gender))
                        {
                            SaveOne(ws, copy, job.Gender, result, s, views, renderSettings);
                            copied++;
                        }
                });
                Log.Write($"Batch: {job} -> {outcome} \"{result.SuggestedName}\" in {result.Elapsed.TotalSeconds:F0}s" + (copied > 0 ? $", copied to {copied} identical" : ""));
                report(new BatchEvent(job, outcome, result.SuggestedName, result.Cost, copied));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e)
            {
                // API failures are self-explanatory; anything else gets its stack trace in the log.
                Log.Write($"Batch: {job} failed: {(e is VisionException ? e.Message : e.ToString())}");
                report(new BatchEvent(job, BatchOutcome.Failed, e.Message));
            }
        });
    }

    private static BatchOutcome SaveOne(Workspace ws, ItemEntry item, ItemGender gender, DescriptionResult result, BatchSettings s,
        IReadOnlyList<Render.D3D.RenderedView> views, string renderSettings)
    {
        var handEdited = ws.DbRow(item, gender) is { DescriptionModel: "user-edited" };
        if (s.WriteToSkyrimNet && !handEdited)
        {
            ws.Save(item, gender, result.SuggestedName, result.Description, s.Model.Model, edited: false, views, renderSettings);
            return BatchOutcome.Saved;
        }
        ws.SaveDraft(item, gender, result.SuggestedName, result.Description, s.Model.Model, views, renderSettings);
        return s.WriteToSkyrimNet ? BatchOutcome.KeptAsDraft : BatchOutcome.Drafted;
    }

    /// <summary>
    /// Has no description from ItemNet (catalog entry, draft or ItemNet row) and none hand-edited in
    /// SkyrimNet. SkyrimNet's automatic descriptions don't count.
    /// </summary>
    public static bool IsUndescribed(Workspace ws, ItemEntry e, ItemGender g)
    {
        if (ws.CatalogEntry(e, g) is not null) return false;
        var model = ws.DbRow(e, g)?.DescriptionModel;
        return model != "user-edited" && model?.StartsWith("ItemNet", StringComparison.Ordinal) != true;
    }

    /// <summary>
    /// Folds identical-looking items into the jobs: each job gets the un-described items that share
    /// its visual key (searched across the whole item list), and those items' own jobs are dropped.
    /// </summary>
    public static List<BatchJob> FoldIdentical(Workspace ws, IReadOnlyList<BatchJob> jobs, out int folded)
    {
        var keys = new Dictionary<(ItemEntry, ItemGender), string?>();
        string? Key(ItemEntry e, ItemGender g)
        {
            if (!keys.TryGetValue((e, g), out var k))
                keys[(e, g)] = k = Core.Items.RenderPlanner.VisualKey(ws.Source.Game, ws.Armor(e), g);
            return k;
        }
        var byKey = new Dictionary<(string, ItemGender), List<ItemEntry>>();
        foreach (var g in jobs.Select(j => j.Gender).Distinct())
            foreach (var e in ws.Items)
                if (Key(e, g) is { } k)
                {
                    if (!byKey.TryGetValue((k, g), out var list)) byKey[(k, g)] = list = [];
                    list.Add(e);
                }

        var taken = new HashSet<(ItemEntry, ItemGender)>();
        var result = new List<BatchJob>();
        folded = 0;
        foreach (var job in jobs)
        {
            if (!taken.Add((job.Item, job.Gender))) { folded++; continue; } // already a copy of an earlier job
            var copies = Key(job.Item, job.Gender) is { } k && byKey.TryGetValue((k, job.Gender), out var same)
                ? same.Where(e => e != job.Item && !taken.Contains((e, job.Gender)) && IsUndescribed(ws, e, job.Gender)).ToList()
                : [];
            foreach (var c in copies) taken.Add((c, job.Gender));
            result.Add(job with { Copies = copies });
        }
        return result;
    }
}
