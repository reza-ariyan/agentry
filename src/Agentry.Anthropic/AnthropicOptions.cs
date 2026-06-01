namespace Agentry;

/// <summary>Configuration for the Anthropic <see cref="IChatModel"/> adapter.</summary>
public sealed class AnthropicOptions
{
    /// <summary>The model id used when none is set. Anthropic model ids change/retire over time — set <see cref="Model"/> explicitly for production.</summary>
    public const string DefaultModel = "claude-haiku-4-5";

    /// <summary>Anthropic API key (<c>sk-ant-...</c>). Required.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Default model id (see <see cref="DefaultModel"/>). Prefer setting this explicitly — ids change over time.</summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary><c>anthropic-version</c> header value.</summary>
    public string Version { get; set; } = "2023-06-01";

    /// <summary>API base URL (override for proxies / gateways).</summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com/";
}
