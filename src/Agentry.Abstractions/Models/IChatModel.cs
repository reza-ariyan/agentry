namespace Agentry;

/// <summary>
/// The provider-agnostic model seam. Implement once per provider (e.g. Anthropic, OpenAI);
/// the agent loop talks only to this, never to a vendor SDK.
/// </summary>
public interface IChatModel
{
    /// <summary>Send the conversation (plus tools) to the model and get one response turn back.</summary>
    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A single request to an <see cref="IChatModel"/>.</summary>
public sealed record ModelRequest
{
    /// <summary>System prompt / instructions.</summary>
    public string? System { get; init; }

    /// <summary>The conversation so far, in order.</summary>
    public required IReadOnlyList<AgentMessage> Messages { get; init; }

    /// <summary>Tools the model may call this turn.</summary>
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];

    /// <summary>Provider model id (e.g. <c>claude-haiku-4-5</c>). Null lets the adapter choose a default.</summary>
    public string? Model { get; init; }

    /// <summary>Maximum tokens to generate.</summary>
    public int MaxTokens { get; init; } = 4096;
}

/// <summary>One response turn from an <see cref="IChatModel"/>.</summary>
public sealed record ModelResponse
{
    /// <summary>Assistant text, if any.</summary>
    public string? Text { get; init; }

    /// <summary>Tool calls the model requested, if any.</summary>
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

    /// <summary>Why the model stopped.</summary>
    public StopReason StopReason { get; init; }

    /// <summary>Token usage for this call.</summary>
    public TokenUsage Usage { get; init; }

    /// <summary>Error detail when <see cref="StopReason"/> is <see cref="StopReason.Error"/>.</summary>
    public string? Error { get; init; }
}
