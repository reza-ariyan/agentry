namespace Agentry;

/// <summary>Who authored a message in the conversation.</summary>
public enum MessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>Why a run (or a model turn) stopped.</summary>
public enum StopReason
{
    /// <summary>The model finished its turn normally.</summary>
    EndTurn,

    /// <summary>The model requested one or more tool calls.</summary>
    ToolCalls,

    /// <summary>Output was truncated at the model's token limit.</summary>
    MaxTokens,

    /// <summary>The agent loop hit its iteration safety cap (<c>AgentRequest.MaxIterations</c>) before the model ended its turn.</summary>
    MaxIterations,

    /// <summary>The provider returned an error (see <c>Error</c>).</summary>
    Error,
}

/// <summary>Token usage for a single model call.</summary>
public readonly record struct TokenUsage(int InputTokens, int OutputTokens)
{
    /// <summary>Total tokens (input + output).</summary>
    public int Total => InputTokens + OutputTokens;

    /// <summary>Sum two usages (used to accumulate across a multi-turn run).</summary>
    public static TokenUsage operator +(TokenUsage a, TokenUsage b)
        => new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens);
}

/// <summary>
/// A neutral, provider-agnostic conversation message. Providers map this to/from their wire format,
/// and <see cref="IConversationStore"/> implementations persist it.
/// </summary>
/// <param name="Role">Who authored the message.</param>
/// <param name="Text">Free-text content, if any.</param>
public sealed record AgentMessage(MessageRole Role, string? Text = null)
{
    /// <summary>Tool calls requested by an <see cref="MessageRole.Assistant"/> message.</summary>
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

    /// <summary>Tool results carried by a <see cref="MessageRole.Tool"/> message.</summary>
    public IReadOnlyList<ToolResult> ToolResults { get; init; } = [];

    /// <summary>Create a system message.</summary>
    public static AgentMessage System(string text) => new(MessageRole.System, text);

    /// <summary>Create a user message.</summary>
    public static AgentMessage User(string text) => new(MessageRole.User, text);

    /// <summary>Create an assistant message (optional text plus any tool calls it requested).</summary>
    public static AgentMessage Assistant(string? text, IReadOnlyList<ToolCall> toolCalls)
        => new(MessageRole.Assistant, text) { ToolCalls = toolCalls };

    /// <summary>Create a tool message carrying the results of the assistant's tool calls.</summary>
    public static AgentMessage Tool(IReadOnlyList<ToolResult> results)
        => new(MessageRole.Tool) { ToolResults = results };
}
