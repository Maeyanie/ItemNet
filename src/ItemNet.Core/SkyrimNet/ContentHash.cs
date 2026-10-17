using System.Text;
using ItemNet.Core.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace ItemNet.Core.SkyrimNet;

/// <summary>
/// Reproduces OmniSight's content_hash (SkyrimNet beta 27,
/// ItemModelRenderer::ComputeContentHash + StandaloneItemRenderer::ComputeContentHash):
///   FNV-1a 64 over  EditorID + "|" + modelPath + ("|" + texture)*,  formatted "%016llX".
/// modelPath is the first ARMA's biped model for the gender (else the other gender's), or the
/// weapon model. Textures are every path of every alternate texture set on the item's own model
/// and on each ARMA's male and female models, sorted ordinally and de-duplicated.
/// The EditorID is the one the game keeps: the last override from a master-flagged plugin, else
/// the winning record's (non-master overrides such as Requiem.esp's renames never reach it).
/// </summary>
public static class ContentHash
{
    public static string? Compute(GameData game, ISkyrimMajorRecordGetter item, bool female)
    {
        var (modelPath, texSources) = item switch
        {
            IArmorGetter armo => ArmorInputs(game, armo, female),
            IWeaponGetter weap => (weap.Model?.File.GivenPath, new List<IModelGetter?> { weap.Model }),
            _ => (null, []),
        };
        if (string.IsNullOrEmpty(modelPath)) return null;

        var textures = texSources
            .Where(m => m?.AlternateTextures is not null)
            .SelectMany(m => m!.AlternateTextures!)
            .Select(at => at.NewTexture.TryResolve(game.LinkCache))
            .Where(t => t is not null)
            .SelectMany(t => new[] { t!.Diffuse, t.NormalOrGloss, t.EnvironmentMaskOrSubsurfaceTint, t.GlowOrDetailMap,
                                     t.Height, t.Environment, t.Multilayer, t.BacklightMaskOrSpecular })
            .Select(p => p?.GivenPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        var sb = new StringBuilder();
        var edid = RuntimeEditorId(game, item);
        if (!string.IsNullOrEmpty(edid)) sb.Append(edid).Append('|');
        sb.Append(modelPath);
        foreach (var t in textures) sb.Append('|').Append(t);
        return Fnv1a64(sb.ToString()).ToString("X16");
    }

    private static (string?, List<IModelGetter?>) ArmorInputs(GameData game, IArmorGetter armo, bool female)
    {
        var addons = armo.Armature.Select(l => l.TryResolve(game.LinkCache)).Where(a => a is not null).ToList();
        var first = addons.FirstOrDefault();
        var model = (female ? first?.WorldModel?.Female : first?.WorldModel?.Male)?.File.GivenPath;
        if (string.IsNullOrEmpty(model)) model = (female ? first?.WorldModel?.Male : first?.WorldModel?.Female)?.File.GivenPath;

        var sources = new List<IModelGetter?> { female ? armo.WorldModel?.Female?.Model : armo.WorldModel?.Male?.Model };
        foreach (var a in addons)
        {
            sources.Add(a!.WorldModel?.Female);
            sources.Add(a.WorldModel?.Male);
        }
        return (model, sources);
    }

    /// <summary>The EditorID the running game reports for a form.</summary>
    public static string? RuntimeEditorId(GameData game, ISkyrimMajorRecordGetter record)
    {
        string? lastMaster = null;
        foreach (var ctx in game.LinkCache.ResolveAllContexts<ISkyrimMajorRecord, ISkyrimMajorRecordGetter>(record.FormKey))
        {
            // Contexts come winner first; the first master-flagged one is the last loaded master.
            if (game.GetPlugin(ctx.ModKey) is { IsMaster: true } && ctx.Record.EditorID is { } e) { lastMaster = e; break; }
        }
        return lastMaster ?? record.EditorID;
    }

    public static ulong Fnv1a64(string s)
    {
        ulong h = 0xCBF29CE484222325;
        foreach (var b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 0x100000001B3; }
        return h;
    }
}
