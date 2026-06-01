namespace Agentry;

/// <summary>
/// A tool the agent can call. <typeparamref name="TContext"/> is your per-run state,
/// threaded through every tool call within a single agent run.
/// </summary>
/// <typeparam name="TContext">Per-run session state (e.g. created-resource maps, ids, flags).</typeparam>
public interface ITool<in TContext>
{
    /// <summary>Unique tool name the model calls (e.g. <c>create_user</c>).</summary>
    string Name { get; }

    /// <summary>Natural-language description shown to the model.</summary>
    string Description { get; }

    /// <summary>JSON Schema object describing the tool's arguments.</summary>
    object InputSchema { get; }

    /// <summary>Execute the tool. Prefer returning a failed <see cref="ToolResult"/> over throwing.</summary>
    Task<ToolResult> ExecuteAsync(ToolCall call, TContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Dispatches a <see cref="ToolCall"/> to the matching <see cref="ITool{TContext}"/>.
/// Implementations never throw: unknown tools and exceptions become failed <see cref="ToolResult"/>s
/// so the loop can recover instead of crashing.
/// </summary>
public interface IToolExecutor<in TContext>
{
    /// <summary>The definitions of every registered tool, for handing to the model.</summary>
    IReadOnlyList<ToolDefinition> Definitions { get; }

    /// <summary>Look up and execute the tool named by <paramref name="call"/>.</summary>
    Task<ToolResult> ExecuteAsync(ToolCall call, TContext context, CancellationToken cancellationToken = default);
}
