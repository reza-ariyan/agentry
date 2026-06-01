namespace Agentry;

/// <summary>Configuration for the Anthropic <see cref="IChatModel"/> adapter.</summary>
public sealed class AnthropicOptions
{
    /// <summary>Anthropic API key (<c>sk-ant-...</c>). Required.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Default model id used when a request doesn't specify one.</summary>
    public string Model { get; set; } = "claude-haiku-4-5";

    /// <summary><c>anthropic-version</c> header value.</summary>
    public string Version { get; set; } = "2023-06-01";

    /// <summary>API base URL (override for proxies / gateways).</summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com/";
}
