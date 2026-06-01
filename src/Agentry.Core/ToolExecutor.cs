using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentry;

/// <summary>
/// Default <see cref="IToolExecutor{TContext}"/>: dispatches by tool name (case-insensitive) and
/// never throws at runtime — unknown tools and exceptions become failed <see cref="ToolResult"/>s so
/// the agent loop can recover. Construction validates tool names and rejects duplicates.
/// </summary>
public sealed class ToolExecutor<TContext> : IToolExecutor<TContext>
{
    // Most providers require tool names to match this pattern.
    private static readonly Regex NamePattern = new("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.Compiled);

    private readonly Dictionary<string, ITool<TContext>> _tools;
    private readonly ILogger _logger;

    /// <summary>Create an executor over the given tools. Throws on invalid or duplicate tool names.</summary>
    public ToolExecutor(IEnumerable<ITool<TContext>> tools, ILogger<ToolExecutor<TContext>>? logger = null)
    {
        _tools = new Dictionary<string, ITool<TContext>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name) || !NamePattern.IsMatch(tool.Name))
                throw new InvalidOperationException(
                    $"Invalid tool name '{tool.Name}'. Tool names must match ^[a-zA-Z0-9_-]{{1,64}}$ (provider requirement).");

            if (!_tools.TryAdd(tool.Name, tool))
                throw new InvalidOperationException($"Duplicate tool name '{tool.Name}' (tool names are case-insensitive).");
        }

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
