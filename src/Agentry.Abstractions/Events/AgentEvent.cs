namespace Agentry;

/// <summary>
/// Progress events emitted by the agent loop as it runs. Consume the stream to drive an SSE
/// endpoint, a log, a UI, or ignore it entirely.
/// </summary>
public abstract record AgentEvent
{
    /// <summary>The run has started.</summary>
    public sealed record Started(string RunId) : AgentEvent;

    /// <summary>The assistant produced text this turn.</summary>
    public sealed record AssistantText(string Text) : AgentEvent;

    /// <summary>A tool is about to execute.</summary>
    public sealed record ToolStarted(string CallId, string ToolName) : AgentEvent;

    /// <summary>A tool finished executing.</summary>
    public sealed record ToolFinished(string CallId, string ToolName, bool Success) : AgentEvent;

    /// <summary>Cumulative token usage was updated.</summary>
    public sealed record UsageUpdated(TokenUsage Cumulative) : AgentEvent;

    /// <summary>The run completed.</summary>
    public sealed record Completed(StopReason Reason, TokenUsage TotalUsage) : AgentEvent;

    /// <summary>The run failed.</summary>
    public sealed record Failed(string Error) : AgentEvent;
}
