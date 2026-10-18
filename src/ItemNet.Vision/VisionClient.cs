using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ItemNet.Vision;

public sealed record DescriptionResult(
    string SuggestedName,
    string Description,
    string Model,
    string RawContent,
    int? PromptTokens,
    int? CompletionTokens,
    double? Cost,
    TimeSpan Elapsed,
    /// <summary>The model's reasoning trace, when the provider returns one.</summary>
    string? Reasoning,
    /// <summary>The whole response body, pretty-printed.</summary>
    string FullResponse);

public sealed class VisionException(string message, string? rawResponse = null) : Exception(message)
{
    /// <summary>The whole response body when there was one (pretty-print with <see cref="VisionClient.Pretty"/>).</summary>
    public string? RawResponse { get; } = rawResponse;
}

/// <summary>Sends the prompt and images to an OpenAI-compatible chat-completions endpoint.</summary>
public sealed partial class VisionClient(HttpClient http)
{
    public const string ProjectUrl = "https://github.com/Maeyanie/ItemNet";

    public async Task<DescriptionResult> DescribeAsync(ProviderConfig provider, VisionModelConfig model, string prompt,
        IReadOnlyList<byte[]> pngImages, CancellationToken ct = default)
    {
        var body = BuildBody(provider, model, prompt, pngImages);
        using var request = new HttpRequestMessage(HttpMethod.Post, provider.Endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(provider.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        // OpenRouter attribution headers; other providers ignore them.
        request.Headers.TryAddWithoutValidation("X-Title", "ItemNet");
        request.Headers.TryAddWithoutValidation("HTTP-Referer", ProjectUrl);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, model.TimeoutSeconds)));
        var started = DateTime.UtcNow;
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new VisionException($"{(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(text, 600)}", text);

        JsonNode? json;
        try { json = JsonNode.Parse(text); }
        catch (JsonException) { throw new VisionException("Response was not JSON: " + Truncate(text, 600), text); }
        if (json?["error"] is { } err) throw new VisionException("Provider error: " + Truncate(err.ToJsonString(), 600), text);

        var message = json?["choices"]?[0]?["message"];
        var content = message?["content"] switch
        {
            JsonValue v => v.GetValue<string>(),
            JsonArray parts => string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? "")),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(content))
            throw new VisionException("The model returned no content (it may have used its whole token budget on reasoning).", text);

        string name, description;
        try { (name, description) = ParseDescription(content); }
        catch (VisionException e) { throw new VisionException(e.Message, text); } // keep the whole body for the raw view

        var usage = json?["usage"];
        return new DescriptionResult(name, description, json?["model"]?.GetValue<string>() ?? model.Model, content,
            usage?["prompt_tokens"]?.GetValue<int>(), usage?["completion_tokens"]?.GetValue<int>(),
            usage?["cost"] is JsonValue c && c.TryGetValue<double>(out var cost) ? cost : null,
            DateTime.UtcNow - started, ExtractReasoning(message, content), Pretty(text));
    }

    /// <summary>
    /// The reasoning trace, wherever this provider put it: OpenRouter's "reasoning" and
    /// "reasoning_details", "reasoning_content" (DeepSeek, vLLM, llama.cpp), or &lt;think&gt; tags
    /// inside the content.
    /// </summary>
    private static string? ExtractReasoning(JsonNode? message, string content)
    {
        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

        if (Str(message?["reasoning"]) is { } r) return r;
        if (Str(message?["reasoning_content"]) is { } rc) return rc;
        if (message?["reasoning_details"] is JsonArray details)
        {
            var parts = details.Select(d => Str(d?["text"]) ?? Str(d?["summary"])).Where(s => s is not null).ToList();
            if (parts.Count > 0) return string.Join("\n\n", parts);
        }
        var think = ThinkTags().Matches(content).Select(m => m.Value[7..^8].Trim()).Where(s => s.Length > 0).ToList();
        return think.Count > 0 ? string.Join("\n\n", think) : null;
    }

    /// <summary>Indented JSON with readable (unescaped) text, or the input unchanged if it isn't JSON.</summary>
    public static string Pretty(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            return JsonNode.Parse(body)?.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) ?? body;
        }
        catch (JsonException) { return body; }
    }

    public static JsonObject BuildBody(ProviderConfig provider, VisionModelConfig model, string prompt, IReadOnlyList<byte[]> pngImages)
    {
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = prompt } };
        foreach (var png in pngImages)
            content.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(png) },
            });

        var body = new JsonObject
        {
            ["model"] = model.Model,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } },
            ["max_tokens"] = model.MaxTokens,
            ["stream"] = false,
        };
        if (model.Temperature is { } t) body["temperature"] = t;
        if (model.TopP is { } p) body["top_p"] = p;
        if (model.TopK is { } k) body["top_k"] = k;
        if (!string.IsNullOrWhiteSpace(model.ReasoningEffort) && model.ReasoningEffort is not ("off" or "none"))
            body["reasoning"] = new JsonObject { ["effort"] = model.ReasoningEffort };

        if (provider.ExtraBody is not null) Merge(body, provider.ExtraBody);
        if (!string.IsNullOrWhiteSpace(model.ExtraJson) && JsonNode.Parse(model.ExtraJson) is JsonObject extra) Merge(body, extra);
        return body;
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject so && target[key] is JsonObject to) Merge(to, so);
            else target[key] = value?.DeepClone();
        }
    }

    /// <summary>
    /// Pulls {"suggestedName", "description"} out of a reply, tolerating reasoning tags, code
    /// fences and text around the JSON.
    /// </summary>
    public static (string name, string description) ParseDescription(string content)
    {
        var text = ThinkTags().Replace(content, "");
        text = text.Replace("```json", "").Replace("```", "");
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new VisionException("No JSON object in the reply: " + Truncate(content, 400), content);

        JsonObject? obj;
        try { obj = JsonNode.Parse(text[start..(end + 1)]) as JsonObject; }
        catch (JsonException e) { throw new VisionException($"Reply JSON is malformed ({e.Message}): {Truncate(content, 400)}", content); }

        string? Field(params string[] names) => obj?.FirstOrDefault(kv => names.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)).Value?.GetValue<string>();
        var name = Field("suggestedName", "suggested_name", "name") ?? "";
        var description = Field("description", "desc") ?? "";
        if (description.Length == 0) throw new VisionException("Reply JSON has no description: " + Truncate(content, 400), content);
        return (name.Trim(), description.Trim());
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkTags();
}
