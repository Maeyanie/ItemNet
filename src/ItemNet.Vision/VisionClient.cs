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

public sealed class VisionException(string message, string? rawResponse = null, bool retryable = false, TimeSpan? retryAfter = null)
    : Exception(message)
{
    /// <summary>The whole response body when there was one (pretty-print with <see cref="VisionClient.Pretty"/>).</summary>
    public string? RawResponse { get; } = rawResponse;
    /// <summary>Worth trying again: rate limits, server errors, dropped connections, malformed replies.</summary>
    public bool Retryable { get; } = retryable;
    /// <summary>The provider's Retry-After, when it sent one.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>One failed attempt that is about to be retried.</summary>
public sealed record RetryNotice(int Attempt, int MaxAttempts, TimeSpan Delay, string Reason);

/// <summary>Sends the prompt and images to an OpenAI-compatible chat-completions endpoint.</summary>
public sealed partial class VisionClient(HttpClient http)
{
    public const string ProjectUrl = "https://github.com/Maeyanie/ItemNet";

    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// HTTP statuses worth retrying. 404 is normally final, but OpenRouter and some others return it
    /// briefly mid-run when a routed provider drops out, so it is retried too.
    /// </summary>
    public static bool IsRetryableStatus(int status) => status is 404 or 408 or 409 or 425 or 429 or >= 500;

    /// <summary>
    /// <see cref="DescribeOnceAsync"/> with up to <see cref="VisionModelConfig.Retries"/> retries and
    /// exponential backoff (2 s doubling, at most 60 s, with jitter; Retry-After wins when sent).
    /// Cancelling <paramref name="ct"/> stops at once, including during a wait.
    /// </summary>
    public async Task<DescriptionResult> DescribeAsync(ProviderConfig provider, VisionModelConfig model, string prompt,
        IReadOnlyList<byte[]> pngImages, CancellationToken ct = default, Action<RetryNotice>? onRetry = null)
    {
        var attempts = 1 + Math.Max(0, model.Retries);
        for (var attempt = 1; ; attempt++)
        {
            Exception failure;
            try
            {
                return await DescribeOnceAsync(provider, model, prompt, pngImages, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { failure = new VisionException($"Timed out after {model.TimeoutSeconds}s", retryable: true); }
            catch (HttpRequestException e) { failure = new VisionException("Connection failed: " + e.Message, retryable: true); }
            catch (IOException e) { failure = new VisionException("Connection failed: " + e.Message, retryable: true); }
            catch (VisionException e) { failure = e; }

            var v = (VisionException)failure;
            if (!v.Retryable || attempt >= attempts)
                throw attempt == 1 ? v : new VisionException($"Gave up after {attempt} attempts. Last error: {v.Message}", v.RawResponse);

            var backoff = BaseDelay * Math.Pow(2, attempt - 1) * (0.75 + Random.Shared.NextDouble() * 0.5);
            var delay = v.RetryAfter is { } ra ? ra : backoff;
            if (delay > MaxDelay) delay = MaxDelay;
            onRetry?.Invoke(new RetryNotice(attempt, attempts, delay, v.Message));
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>One request, no retries. Failures worth retrying throw a retryable <see cref="VisionException"/>.</summary>
    public async Task<DescriptionResult> DescribeOnceAsync(ProviderConfig provider, VisionModelConfig model, string prompt,
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
        {
            var status = (int)response.StatusCode;
            throw new VisionException($"{status} {response.ReasonPhrase}: {Truncate(text, 600)}", text,
                IsRetryableStatus(status), RetryAfter(response));
        }

        JsonNode? json;
        try { json = ParseLenient(text); }
        catch (JsonException) { throw new VisionException("Response was not JSON: " + Truncate(text, 600), text, retryable: true); }
        // Errors can also arrive inside a 200, at the top level or on the choice (OpenRouter does both
        // when an upstream provider fails mid-request). Retry unless the code says it's final.
        if ((json?["error"] ?? json?["choices"]?[0]?["error"]) is { } err)
        {
            var code = err["code"] is JsonValue cv && cv.TryGetValue<int>(out var errCode) ? errCode : (int?)null;
            throw new VisionException("Provider error: " + Truncate(err.ToJsonString(), 600), text, code is null || IsRetryableStatus(code.Value));
        }

        var message = json?["choices"]?[0]?["message"];
        var content = message?["content"] switch
        {
            JsonValue v => v.GetValue<string>(),
            JsonArray parts => string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? "")),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(content))
            throw new VisionException("The model returned no content (it may have used its whole token budget on reasoning).", text, retryable: true);

        string name, description;
        try { (name, description) = ParseDescription(content); }
        catch (VisionException e) { throw new VisionException(e.Message, text, retryable: true); } // keep the whole body for the raw view

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
            return ParseLenient(body)?.ToJsonString(new JsonSerializerOptions
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
        try { obj = ParseLenient(text[start..(end + 1)]) as JsonObject; }
        catch (JsonException e) { throw new VisionException($"Reply JSON is malformed ({e.Message}): {Truncate(content, 400)}", content); }

        string? Field(params string[] names) => obj?.FirstOrDefault(kv => names.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)).Value?.GetValue<string>();
        var name = Field("suggestedName", "suggested_name", "name") ?? "";
        var description = Field("description", "desc") ?? "";
        if (description.Length == 0) throw new VisionException("Reply JSON has no description: " + Truncate(content, 400), content);
        return (name.Trim(), description.Trim());
    }

    /// <summary>
    /// <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/>, but a repeated key
    /// keeps its last value (as in JavaScript) instead of failing. JsonObject parses lazily and then
    /// throws ArgumentException ("An item with the same key has already been added") on the first
    /// lookup; models sometimes write "description" twice. Invalid JSON still throws JsonException.
    /// </summary>
    public static JsonNode? ParseLenient(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return ToNode(doc.RootElement);
    }

    private static JsonNode? ToNode(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var p in e.EnumerateObject()) obj[p.Name] = ToNode(p.Value);
                return obj;
            case JsonValueKind.Array:
                return new JsonArray(e.EnumerateArray().Select(ToNode).ToArray());
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return null;
            default:
                return JsonValue.Create(e.Clone());
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra?.Delta is { } d) return d;
        if (ra?.Date is { } date) return date - DateTimeOffset.UtcNow is { Ticks: > 0 } wait ? wait : TimeSpan.Zero;
        return null;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkTags();
}
