using Scriban;
using Scriban.Runtime;

namespace ItemNet.Vision;

/// <summary>What the prompt knows about the item, besides the images.</summary>
public sealed class ItemFacts
{
    public string Name { get; init; } = "";
    public string EditorId { get; init; } = "";
    /// <summary>"Light armor", "Heavy armor" or "Clothing".</summary>
    public string Kind { get; init; } = "";
    /// <summary>Body areas the item occupies, e.g. "torso, hands".</summary>
    public string BodyAreas { get; init; } = "";
    public string Keywords { get; init; } = "";
    public int Value { get; init; }
    public float Weight { get; init; }
    public float ArmorRating { get; init; }
    public string Gender { get; init; } = "female";
    public string Race { get; init; } = "";
    public bool MannequinTextured { get; init; } = true;
    public IReadOnlyList<string> Views { get; init; } = [];
}

/// <summary>
/// The describe prompt, as an editable Scriban template. Variables are snake_case versions of
/// <see cref="ItemFacts"/>; SkyrimNet's names (subject_name, item_type, item_keywords, ...) are
/// also provided so a pasted SkyrimNet prompt needs only its {% %} blocks converted.
/// </summary>
public static class PromptTemplate
{
    public const string Default = """
        Describe the clothing or armor item shown in these {{ views | array.size }} images. The description is for an AI that narrates a fantasy role-playing game and needs to know what characters are wearing.

        The images show the item worn on a {{ gender }} figure, seen from the {{ views | array.join ", " }}. {{ if mannequin_textured }}The figure is only a display stand: never describe the person, their skin or their body, only the item.{{ else }}The plain grey figure is only a display stand: never describe it, only the item.{{ end }}

        Game data (it may not match what is visible; trust the images):
        - Name in game: {{ name }}
        - Kind: {{ kind }}{{ if body_areas != "" }}
        - Worn on: {{ body_areas }}{{ end }}{{ if keywords != "" }}
        - Keywords: {{ keywords }}{{ end }}

        Reply with only a JSON object, no markdown:
        {"suggestedName": "...", "description": "..."}

        suggestedName: 2-5 words naming the item by what it looks like, using its most distinctive colour, material or shape. Do not reuse or rephrase the game name.

        description: exactly 4 sentences.
        1. What kind of item it is, which parts of the body it covers, and which it leaves exposed.
        2. Its main materials and colours, named precisely.
        3. How it is built: the most prominent parts first (hanging strips, plates, straps, fastenings), then the rest.
        4. Any remaining details that set it apart from similar items.

        Describe only what is visible. Do not invent decorations, runes or patterns; if a surface is plain, leave it out. Name exact colours ("oxblood", "ash grey") rather than vague ones ("dark"). Avoid filler such as "appears to be", "featuring" or "intricate design".
        """;

    public static string Render(string template, ItemFacts facts)
    {
        var parsed = Template.Parse(template);
        if (parsed.HasErrors) throw new FormatException("Prompt template error: " + string.Join("; ", parsed.Messages));

        var globals = new ScriptObject
        {
            ["name"] = facts.Name, ["editor_id"] = facts.EditorId, ["kind"] = facts.Kind,
            ["body_areas"] = facts.BodyAreas, ["keywords"] = facts.Keywords, ["value"] = facts.Value,
            ["weight"] = facts.Weight, ["armor_rating"] = facts.ArmorRating, ["gender"] = facts.Gender,
            ["race"] = facts.Race, ["mannequin_textured"] = facts.MannequinTextured, ["views"] = facts.Views.ToArray(),
            // SkyrimNet's describe_item.prompt names.
            ["subject_name"] = facts.Name, ["item_type"] = facts.Kind, ["item_editor_id"] = facts.EditorId,
            ["item_value"] = facts.Value, ["item_weight"] = facts.Weight, ["item_keywords"] = facts.Keywords,
            ["has_mannequin_body"] = true,
        };
        var context = new TemplateContext { StrictVariables = false };
        context.PushGlobal(globals);
        return parsed.Render(context).Trim();
    }
}
