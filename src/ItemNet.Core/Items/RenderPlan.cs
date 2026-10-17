using ItemNet.Core.Plugins;
using ItemNet.Core.SkyrimNet;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace ItemNet.Core.Items;

public enum ModelRole { Item, Body, Head }

/// <summary>The eight texture slots of a TXST / BSShaderTextureSet, Data-relative ("textures\...").</summary>
public sealed record TextureSlots(string?[] Paths)
{
    public static TextureSlots From(ITextureSetGetter t) => new(new[]
    {
        t.Diffuse?.DataRelativePath.Path, t.NormalOrGloss?.DataRelativePath.Path,
        t.EnvironmentMaskOrSubsurfaceTint?.DataRelativePath.Path, t.GlowOrDetailMap?.DataRelativePath.Path,
        t.Height?.DataRelativePath.Path, t.Environment?.DataRelativePath.Path,
        t.Multilayer?.DataRelativePath.Path, t.BacklightMaskOrSpecular?.DataRelativePath.Path,
    });
}

/// <summary>One NIF to draw, plus the record-level overrides that apply to it.</summary>
public sealed record ModelRef(
    ModelRole Role,
    string NifPath,
    bool WeightSlider,
    /// <summary>Alternate texture sets keyed by 3D (shape) name, case-insensitive.</summary>
    IReadOnlyDictionary<string, TextureSlots> AlternateTextures,
    /// <summary>ARMA skin texture: replaces textures on shapes that use the skin shader.</summary>
    TextureSlots? SkinTexture,
    string Source);

/// <summary>Everything needed to render one item on a mannequin for one race and gender.</summary>
public sealed record RenderPlan(
    FormKey Item,
    string ItemName,
    ItemGender Gender,
    FormKey Race,
    string? SkeletonPath,
    IReadOnlyList<ModelRef> Models,
    uint ItemSlots,
    IReadOnlyList<string> Warnings);

public static class RenderPlanner
{
    public static readonly FormKey NordRace = FormKey.Factory("013746:Skyrim.esm");

    public static RenderPlan Plan(GameData game, IArmorGetter armo, ItemGender gender, FormKey? raceKey = null, bool mannequin = true)
    {
        var cache = game.LinkCache;
        var warnings = new List<string>();
        var female = gender == ItemGender.Female;
        var race = cache.Resolve<IRaceGetter>(raceKey ?? NordRace);
        var models = new List<ModelRef>();

        var itemSlots = (uint)(armo.BodyTemplate?.FirstPersonFlags ?? 0);
        foreach (var arma in AddonsFor(game, armo, race))
        {
            var m = AddonModel(game, arma, female, ModelRole.Item);
            if (m is null) warnings.Add($"{arma.EditorID}: no {(female ? "female" : "male")} or fallback model");
            else models.Add(m);
        }
        if (models.Count == 0) warnings.Add($"No armor addon matches {race.EditorID}");

        if (mannequin)
        {
            if (race.Skin.TryResolve(cache) is { } skin)
            {
                foreach (var arma in AddonsFor(game, skin, race))
                {
                    var slots = (uint)(arma.BodyTemplate?.FirstPersonFlags ?? 0);
                    if ((slots & itemSlots) != 0) continue; // covered by the item, as in game
                    if (AddonModel(game, arma, female, ModelRole.Body) is { } m) models.Add(m);
                }
            }
            else warnings.Add($"{race.EditorID} has no skin");

            const uint HeadSlot = 1u << 0; // slot 30
            if ((itemSlots & HeadSlot) == 0 && FaceModel(game, race, female) is { } head) models.Add(head);
        }

        var skeleton = (female ? race.SkeletalModel?.Female : race.SkeletalModel?.Male)?.File.DataRelativePath.Path;
        return new RenderPlan(armo.FormKey, armo.Name?.String ?? armo.EditorID ?? "", gender, race.FormKey, skeleton,
            models, itemSlots, warnings);
    }

    /// <summary>The addons the game would equip on this race: every ARMA valid for it, else for its armor race.</summary>
    public static IEnumerable<IArmorAddonGetter> AddonsFor(GameData game, IArmorGetter armo, IRaceGetter race)
    {
        var addons = armo.Armature.Select(l => l.TryResolve(game.LinkCache)).Where(a => a is not null).Select(a => a!).ToList();
        var direct = addons.Where(a => Fits(a, race.FormKey)).ToList();
        if (direct.Count > 0 || race.ArmorRace.IsNull) return direct;
        return addons.Where(a => Fits(a, race.ArmorRace.FormKey));
    }

    private static bool Fits(IArmorAddonGetter a, FormKey race) =>
        a.Race.FormKey == race || a.AdditionalRaces.Any(r => r.FormKey == race);

    private static ModelRef? AddonModel(GameData game, IArmorAddonGetter arma, bool female, ModelRole role)
    {
        var model = female ? arma.WorldModel?.Female ?? arma.WorldModel?.Male : arma.WorldModel?.Male ?? arma.WorldModel?.Female;
        if (model is null || string.IsNullOrEmpty(model.File.GivenPath)) return null;

        var alt = new Dictionary<string, TextureSlots>(StringComparer.OrdinalIgnoreCase);
        foreach (var at in model.AlternateTextures ?? [])
            if (at.NewTexture.TryResolve(game.LinkCache) is { } txst)
                alt[at.Name] = TextureSlots.From(txst);

        var skinLink = female ? arma.SkinTexture?.Female : arma.SkinTexture?.Male;
        var skin = skinLink?.TryResolve(game.LinkCache) is { } s ? TextureSlots.From(s) : null;
        var weight = female ? arma.WeightSliderEnabled?.Female ?? false : arma.WeightSliderEnabled?.Male ?? false;
        return new ModelRef(role, model.File.DataRelativePath.Path, weight, alt, skin, arma.EditorID ?? arma.FormKey.ToString());
    }

    private static ModelRef? FaceModel(GameData game, IRaceGetter race, bool female)
    {
        var head = female ? race.HeadData?.Female : race.HeadData?.Male;
        foreach (var part in head?.HeadParts ?? [])
        {
            if (part.Head.TryResolve(game.LinkCache) is not { } hdpt) continue;
            if (hdpt.Type != HeadPart.TypeEnum.Face || hdpt.Model is null) continue;
            var tex = hdpt.TextureSet.TryResolve(game.LinkCache) is { } t ? TextureSlots.From(t) : null;
            return new ModelRef(ModelRole.Head, hdpt.Model.File.DataRelativePath.Path, false,
                new Dictionary<string, TextureSlots>(), tex, hdpt.EditorID ?? "");
        }
        return null;
    }
}
