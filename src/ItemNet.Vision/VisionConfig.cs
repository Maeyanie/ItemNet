using System.Text.Json.Nodes;

namespace ItemNet.Vision;

/// <summary>An OpenAI-compatible chat-completions endpoint.</summary>
public sealed class ProviderConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Full chat-completions URL, e.g. https://openrouter.ai/api/v1/chat/completions.</summary>
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    /// <summary>Merged into every request body, e.g. OpenRouter's {"provider": {...}} routing block.</summary>
    public JsonObject? ExtraBody { get; set; }
    public string Source { get; set; } = "manual";

    public override string ToString() => string.IsNullOrEmpty(Name) ? Id : Name;
}

/// <summary>A vision model on one provider, with its sampling settings.</summary>
public sealed class VisionModelConfig
{
    public string Model { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public int MaxTokens { get; set; } = 4096;
    public double? Temperature { get; set; } = 0.7;
    public double? TopP { get; set; }
    public int? TopK { get; set; }
    /// <summary>"low", "medium", "high" or empty/"off" for none. Sent as {"reasoning": {"effort": ...}}.</summary>
    public string? ReasoningEffort { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    /// <summary>Extra attempts after a failure worth retrying (timeouts, 404/408/429/5xx, bad replies).</summary>
    public int Retries { get; set; } = 4;
    /// <summary>Raw JSON merged last into the request body, for anything not covered above.</summary>
    public string? ExtraJson { get; set; }

    public override string ToString() => $"{Model} ({ProviderId})";
}
