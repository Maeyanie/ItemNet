using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Serialization;

namespace ItemNet.Vision;

/// <summary>
/// Reads SkyrimNet's provider list (config/Providers.yaml) and the vision model routing of its
/// active model preset (config/model_presets/&lt;active_preset_id&gt;.yaml, variants.vision).
/// </summary>
public static class SkyrimNetImport
{
    public sealed record Result(List<ProviderConfig> Providers, List<VisionModelConfig> VisionModels, List<string> Notes);

    public static Result Import(string configDir)
    {
        var notes = new List<string>();
        var providers = new List<ProviderConfig>();
        var models = new List<VisionModelConfig>();

        var providersFile = Path.Combine(configDir, "Providers.yaml");
        if (File.Exists(providersFile))
        {
            var root = Load(providersFile) as Dictionary<object, object>;
            var list = root?.GetValueOrDefault("providers") as List<object> ?? [];
            var raw = list.OfType<Dictionary<object, object>>().ToList();
            foreach (var p in raw)
            {
                if (Str(p, "api_style") is { } style && style != "chat") continue; // e.g. OpenRouter "decisions"
                var key = Str(p, "api_key") ?? "";
                if (string.IsNullOrEmpty(key) && Str(p, "api_key_from") is { } from)
                    key = raw.FirstOrDefault(o => Str(o, "id") == from) is { } src ? Str(src, "api_key") ?? "" : "";
                providers.Add(new ProviderConfig
                {
                    Id = Str(p, "id") ?? "",
                    Name = Str(p, "name") ?? Str(p, "id") ?? "",
                    Endpoint = Str(p, "endpoint") ?? "",
                    ApiKey = key,
                    ExtraBody = p.GetValueOrDefault("provider_settings") is Dictionary<object, object> ps ? ToJson(ps) as JsonObject : null,
                    Source = "SkyrimNet",
                });
            }
            notes.Add($"{providers.Count} providers from Providers.yaml");
        }
        else notes.Add($"No Providers.yaml in {configDir}");

        // Vision routing: the active preset's variants.vision, else any "vision" block in OpenRouter.yaml.
        Dictionary<object, object>? vision = null;
        var orFile = Path.Combine(configDir, "OpenRouter.yaml");
        var orRoot = File.Exists(orFile) ? Load(orFile) : null;
        if (FindKey(orRoot, "active_preset_id") is string presetId && presetId.Length > 0)
        {
            var presetFile = Path.Combine(configDir, "model_presets", presetId + ".yaml");
            if (File.Exists(presetFile) && Load(presetFile) is Dictionary<object, object> preset
                && preset.GetValueOrDefault("variants") is Dictionary<object, object> variants)
            {
                vision = variants.GetValueOrDefault("vision") as Dictionary<object, object>;
                if (vision is not null) notes.Add($"Vision models from preset '{presetId}'");
            }
        }
        vision ??= FindKey(orRoot, "vision") as Dictionary<object, object>;

        if (vision is not null)
        {
            var names = (Str(vision, "model_name") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var paramList = (vision.GetValueOrDefault("model_params") as List<object> ?? []).OfType<Dictionary<object, object>>().ToList();
            var defaults = vision.GetValueOrDefault("default_params") as Dictionary<object, object>;
            for (var i = 0; i < names.Length; i++)
            {
                // model_params pairs with model_name by position; the last entry covers any extras.
                var mp = paramList.Count == 0 ? null : paramList[Math.Min(i, paramList.Count - 1)];
                string? Get(string k) => (mp is not null ? Str(mp, k) : null) ?? (defaults is not null ? Str(defaults, k) : null);
                var effort = Get("reasoning_effort") ?? Str(vision, "reasoning_effort");
                models.Add(new VisionModelConfig
                {
                    Model = names[i],
                    ProviderId = Get("provider_id") ?? providers.FirstOrDefault()?.Id ?? "",
                    MaxTokens = Int(Get("max_tokens")) ?? 4096,
                    Temperature = Dbl(Get("temperature")),
                    TopP = Dbl(Get("top_p")),
                    TopK = Int(Get("top_k")),
                    ReasoningEffort = effort is null or "off" or "none" ? null : effort,
                    TimeoutSeconds = Int(Get("request_timeout")) ?? 300,
                });
            }
            notes.Add($"{models.Count} vision model(s)");
        }
        else notes.Add("No vision model routing found");

        return new Result(providers, models, notes);
    }

    private static object? Load(string path) => new DeserializerBuilder().Build().Deserialize<object>(File.ReadAllText(path));

    private static string? Str(Dictionary<object, object> d, string key) =>
        d.TryGetValue(key, out var v) && v is not null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

    private static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static double? Dbl(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Depth-first search for a key anywhere in a YAML tree.</summary>
    private static object? FindKey(object? node, string key)
    {
        switch (node)
        {
            case Dictionary<object, object> d:
                if (d.TryGetValue(key, out var v)) return v;
                foreach (var child in d.Values) if (FindKey(child, key) is { } found) return found;
                break;
            case List<object> l:
                foreach (var child in l) if (FindKey(child, key) is { } found) return found;
                break;
        }
        return null;
    }

    private static JsonNode? ToJson(object? yaml) => yaml switch
    {
        null => null,
        Dictionary<object, object> d => new JsonObject(d.Where(kv => kv.Value is not null)
            .Select(kv => KeyValuePair.Create(Convert.ToString(kv.Key, CultureInfo.InvariantCulture)!, ToJson(kv.Value)))),
        List<object> l => new JsonArray(l.Select(ToJson).ToArray()),
        string s when bool.TryParse(s, out var b) => JsonValue.Create(b),
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => JsonValue.Create(n),
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) => JsonValue.Create(f),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(Convert.ToString(yaml, CultureInfo.InvariantCulture)),
    };
}
