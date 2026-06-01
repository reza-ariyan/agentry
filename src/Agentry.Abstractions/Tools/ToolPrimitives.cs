using System.Text.Json;

namespace Agentry;

/// <summary>A request from the model to invoke a tool.</summary>
/// <param name="Id">Provider-assigned call id, used to correlate the result.</param>
/// <param name="Name">The tool name the model wants to call.</param>
/// <param name="Arguments">The raw JSON arguments the model supplied.</param>
public sealed record ToolCall(string Id, string Name, JsonElement Arguments);

/// <summary>The outcome of executing a <see cref="ToolCall"/>.</summary>
/// <param name="CallId">The <see cref="ToolCall.Id"/> this result corresponds to.</param>
/// <param name="IsSuccess">Whether the tool succeeded. Failures are fed back to the model, not thrown.</param>
/// <param name="Content">The model-facing result text.</param>
/// <param name="Data">Optional structured data for the host application (not sent to the model verbatim).</param>
public sealed record ToolResult(string CallId, bool IsSuccess, string Content, object? Data = null)
{
    /// <summary>Create a successful result correlated to <paramref name="call"/>.</summary>
    public static ToolResult Ok(ToolCall call, string content, object? data = null)
        => new(call.Id, true, content, data);

    /// <summary>Create a failed result correlated to <paramref name="call"/>. The model sees <paramref name="error"/> and can adapt.</summary>
    public static ToolResult Fail(ToolCall call, string error)
        => new(call.Id, false, error);
}

/// <summary>The model-facing declaration of a tool: its name, description, and JSON input schema.</summary>
/// <param name="Name">Unique tool name (e.g. <c>create_user</c>).</param>
/// <param name="Description">Natural-language description the model uses to decide when to call it.</param>
/// <param name="InputSchema">A JSON Schema object describing the tool's arguments.</param>
public sealed record ToolDefinition(string Name, string Description, object InputSchema);
