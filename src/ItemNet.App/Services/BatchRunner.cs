using ItemNet.Core.Items;
using ItemNet.Core.SkyrimNet;
using ItemNet.Render.Scene;
using ItemNet.Vision;

namespace ItemNet.App.Services;

public enum BatchGender { Female, Male, Both }

public sealed record BatchJob(ItemEntry Item, ItemGender Gender)
{
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
public sealed record BatchEvent(BatchJob Job, BatchOutcome? Outcome, string Message, double? Cost = null);

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
                await onUi(() =>
                {
                    var handEdited = ws.DbRow(job.Item, job.Gender) is { DescriptionModel: "user-edited" };
                    if (s.WriteToSkyrimNet && !handEdited)
                    {
                        ws.Save(job.Item, job.Gender, result.SuggestedName, result.Description, s.Model.Model, edited: false, views, renderSettings);
                        outcome = BatchOutcome.Saved;
                    }
                    else
                    {
                        ws.SaveDraft(job.Item, job.Gender, result.SuggestedName, result.Description, s.Model.Model, views, renderSettings);
                        outcome = s.WriteToSkyrimNet ? BatchOutcome.KeptAsDraft : BatchOutcome.Drafted;
                    }
                });
                Log.Write($"Batch: {job} -> {outcome} \"{result.SuggestedName}\" in {result.Elapsed.TotalSeconds:F0}s");
                report(new BatchEvent(job, outcome, result.SuggestedName, result.Cost));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e)
            {
                Log.Write($"Batch: {job} failed: {e.Message}");
                report(new BatchEvent(job, BatchOutcome.Failed, e.Message));
            }
        });
    }
}
