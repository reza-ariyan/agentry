using System.Text.Json;

namespace Agentry;

/// <summary>
/// Ergonomic base class for a tool with a typed input. Derive from this and implement
/// <see cref="ExecuteAsync(TInput, TContext, ToolCall, CancellationToken)"/>; the JSON schema is
/// generated from <typeparamref name="TInput"/> and the model's arguments are deserialized for you.
/// </summary>
/// <typeparam name="TInput">The tool's strongly-typed argument object.</typeparam>
/// <typeparam name="TContext">Per-run session state, threaded through the call.</typeparam>
public abstract class Tool<TInput, TContext> : ITool<TContext>
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    /// <remarks>Generated (and cached) from <typeparamref name="TInput"/>. Override only for full manual control.</remarks>
    public virtual object InputSchema => ToolSchema.For<TInput>();

    /// <summary>Execute the tool with deserialized, strongly-typed <paramref name="input"/>.</summary>
    protected abstract Task<ToolResult> ExecuteAsync(TInput input, TContext context, ToolCall call, CancellationToken cancellationToken);

    Task<ToolResult> ITool<TContext>.ExecuteAsync(ToolCall call, TContext context, CancellationToken cancellationToken)
    {
        TInput input;
        try
        {
            // Treat missing/null arguments as an empty object so tools with all-optional inputs still run.
            input = call.Arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? JsonSerializer.Deserialize<TInput>("{}", ToolSchema.JsonOptions)!
                : call.Arguments.Deserialize<TInput>(ToolSchema.JsonOptions)
                  ?? throw new InvalidOperationException("arguments deserialized to null");
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail(call, $"Invalid arguments for '{Name}': {ex.Message}"));
        }

        return ExecuteAsync(input, context, call, cancellationToken);
    }
}
