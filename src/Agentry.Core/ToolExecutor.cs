using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentry;

/// <summary>
/// Default <see cref="IToolExecutor{TContext}"/>: dispatches by tool name (case-insensitive) and
/// never throws — unknown tools and exceptions become failed <see cref="ToolResult"/>s so the
/// agent loop can recover.
/// </summary>
public sealed class ToolExecutor<TContext> : IToolExecutor<TContext>
{
    private readonly Dictionary<string, ITool<TContext>> _tools;
    private readonly ILogger _logger;

    public ToolExecutor(IEnumerable<ITool<TContext>> tools, ILogger<ToolExecutor<TContext>>? logger = null)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        Definitions = _tools.Values
            .Select(t => new ToolDefinition(t.Name, t.Description, t.InputSchema))
            .ToList();
        _logger = logger ?? NullLogger<ToolExecutor<TContext>>.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> Definitions { get; }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(ToolCall call, TContext context, CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
        {
            _logger.LogWarning("Unknown tool requested: {Tool}", call.Name);
            return ToolResult.Fail(call, $"Unknown tool '{call.Name}'. It is not registered for this agent.");
        }

        try
        {
            return await tool.ExecuteAsync(call, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {Tool} threw", call.Name);
            return ToolResult.Fail(call, $"Tool '{call.Name}' failed: {ex.Message}");
        }
    }
}
