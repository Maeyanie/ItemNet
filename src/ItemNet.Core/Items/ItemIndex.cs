using ItemNet.Core.Plugins;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace ItemNet.Core.Items;

/// <summary>One wearable item as the list shows it.</summary>
public sealed record ItemEntry(
    FormKey Key,
    string Name,
    string EditorId,
    string Plugin,
    uint? RuntimeId,
    string Kind,
    uint Slots,
    string SlotNames,
    string BodyAreas,
    IReadOnlyList<string> Keywords,
    int Value,
    float Weight,
    float ArmorRating,
    bool HasFemaleModel,
    bool HasMaleModel)
{
    public string FormKeyString => Key.ToString();
}

/// <summary>Every playable, modelled armour in the load order (winning overrides).</summary>
public static class ItemIndex
{
    // Biped slots 30-61, as named in the Creation Kit and SkyrimNet's body areas.
    private static readonly string[] SlotNames =
    [
        "Head", "Hair", "Body", "Hands", "Forearms", "Amulet", "Ring", "Feet", "Calves", "Shield",
        "Tail", "LongHair", "Circlet", "Ears", "Face/Mouth", "Neck", "Chest", "Back", "Misc", "Pelvis",
        "DecapitatedHead", "Decapitate", "Pelvis2", "Leg", "Leg2", "FaceAlt", "Chest2", "Shoulder", "ArmLeft",
        "ArmRight", "Misc2", "FX",
    ];

    private static readonly Dictionary<int, string> AreaBySlot = new()
    {
        [0] = "head", [1] = "hair", [2] = "torso", [3] = "hands", [4] = "forearms", [5] = "neck", [6] = "finger",
        [7] = "feet", [8] = "calves", [9] = "off hand", [12] = "forehead", [13] = "ears", [14] = "face",
        [15] = "neck", [16] = "chest", [17] = "back", [19] = "pelvis", [22] = "pelvis", [23] = "legs",
        [24] = "legs", [26] = "chest", [27] = "shoulders", [28] = "left arm", [29] = "right arm",
    };

    public static List<ItemEntry> Build(GameData game)
    {
        var keywordNames = new Dictionary<FormKey, string>();
        string Keyword(FormKey k)
        {
            if (!keywordNames.TryGetValue(k, out var n))
                keywordNames[k] = n = game.LinkCache.TryResolve<IKeywordGetter>(k, out var kw) ? kw.EditorID ?? "" : "";
            return n;
        }

        var result = new List<ItemEntry>();
        foreach (var armo in game.LoadOrder.PriorityOrder.Armor().WinningOverrides())
        {
            if (armo.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable)) continue;
            var name = armo.Name?.String;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var addons = armo.Armature.Select(a => a.TryResolve(game.LinkCache)).Where(a => a is not null).ToList();
            var female = addons.Any(a => !string.IsNullOrEmpty(a!.WorldModel?.Female?.File.GivenPath));
            var male = addons.Any(a => !string.IsNullOrEmpty(a!.WorldModel?.Male?.File.GivenPath));
            if (!female && !male) continue;

            var slots = (uint)(armo.BodyTemplate?.FirstPersonFlags ?? 0);
            var kind = armo.BodyTemplate?.ArmorType switch
            {
                ArmorType.LightArmor => "Light armor",
                ArmorType.HeavyArmor => "Heavy armor",
                _ => "Clothing",
            };
            result.Add(new ItemEntry(
                armo.FormKey, name, armo.EditorID ?? "", armo.FormKey.ModKey.FileName, game.ToRuntimeFormId(armo.FormKey),
                kind, slots, Describe(slots, SlotNames, ", "), BodyAreasOf(slots),
                (armo.Keywords ?? []).Select(k => Keyword(k.FormKey)).Where(k => k.Length > 0).ToList(),
                (int)armo.Value, armo.Weight, armo.ArmorRating, female, male));
        }
        return result;
    }

    public static string BodyAreasOf(uint slots) =>
        string.Join(", ", Enumerable.Range(0, 32).Where(i => (slots & (1u << i)) != 0)
            .Select(i => AreaBySlot.GetValueOrDefault(i)).Where(a => a is not null).Distinct());

    private static string Describe(uint slots, string[] names, string sep) =>
        string.Join(sep, Enumerable.Range(0, 32).Where(i => (slots & (1u << i)) != 0).Select(i => $"{i + 30} {names[i]}"));
}
