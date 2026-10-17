using ItemNet.Core.Items;
using ItemNet.Vision;

namespace ItemNet.Cli;

public static partial class Commands
{
    /// <summary>Shows what an import from SkyrimNet's config would bring in (keys masked).</summary>
    public static int ImportSkyrimNet(CliOptions opts)
    {
        var dir = opts.Get("config", @"G:\MO2Data\SkyrimSE\overwrite\SKSE\Plugins\SkyrimNet\config");
        var r = SkyrimNetImport.Import(dir);
        foreach (var n in r.Notes) Console.WriteLine(n);
        foreach (var p in r.Providers)
            Console.WriteLine($"  provider {p.Id,-22} {p.Endpoint,-60} key={(p.ApiKey.Length > 0 ? "set" : "-")} extra={p.ExtraBody?.ToJsonString() ?? "-"}");
        foreach (var m in r.VisionModels)
            Console.WriteLine($"  vision   {m.Model} via {m.ProviderId}: max_tokens={m.MaxTokens} temp={m.Temperature} top_p={m.TopP} top_k={m.TopK} reasoning={m.ReasoningEffort ?? "-"} timeout={m.TimeoutSeconds}s");
        return 0;
    }

    /// <summary>Renders the describe prompt for an item, and optionally the request body (images elided).</summary>
    public static int Prompt(CliOptions opts)
    {
        using var s = Session.Open(opts, verbose: false);
        var armo = ResolveArmor(s, opts.Positional[0]);
        var entry = ItemIndex.Build(s.Game).First(e => e.Key == armo.FormKey);
        var template = opts.Get("template") is { } t ? File.ReadAllText(t) : PromptTemplate.Default;
        var prompt = PromptTemplate.Render(template, FactsFor(entry, opts.Has("male") ? "male" : "female", !opts.Has("clay")));
        Console.WriteLine(prompt);
        return 0;
    }

    public static ItemFacts FactsFor(ItemEntry e, string gender, bool textured) => new()
    {
        Name = e.Name, EditorId = e.EditorId, Kind = e.Kind, BodyAreas = e.BodyAreas,
        Keywords = string.Join(", ", e.Keywords), Value = e.Value, Weight = e.Weight, ArmorRating = e.ArmorRating,
        Gender = gender, MannequinTextured = textured, Views = ["front", "right", "back", "left"],
    };
}
