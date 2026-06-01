using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentry;

/// <summary>Runs an agent: the model ↔ tool loop, streaming <see cref="AgentEvent"/>s as it goes.</summary>
public interface IAgentRunner<TContext>
{
    /// <summary>Run the agent to completion, yielding progress events. Resumes if <see cref="AgentRequest.RunId"/> has prior history.</summary>
    IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, TContext state, CancellationToken cancellationToken = default);
}

/// <summary>Tuning knobs for the agent loop.</summary>
public sealed class AgentryOptions
{
    /// <summary>Max consecutive transient-error retries before failing the run.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base backoff in ms; doubles each retry (1s, 2s, 4s, ...).</summary>
    public int RetryBaseDelayMs { get; set; } = 1000;
}

/// <summary>One agent run's inputs (the per-run state is passed separately and typed).</summary>
public sealed record AgentRequest
{
    /// <summary>Stable id for persistence + resume. Defaults to a new id per run.</summary>
    public string? RunId { get; init; }

    /// <summary>System prompt / instructions.</summary>
    public string? System { get; init; }

    /// <summary>The user message that starts (or continues) the run. Optional when resuming.</summary>
    public string? Prompt { get; init; }

    /// <summary>Provider model id; null lets the adapter choose.</summary>
    public string? Model { get; init; }

    /// <summary>Max tokens per model call.</summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>Hard cap on loop iterations (safety against runaway agents).</summary>
    public int MaxIterations { get; init; } = 50;
}

/// <summary>The default agent loop.</summary>
public sealed class AgentRunner<TContext>(
    IChatModel model,
    IToolExecutor<TContext> executor,
    IConversationStore store,
    AgentryOptions? options = null,
    ILogger<AgentRunner<TContext>>? logger = null) : IAgentRunner<TContext>
{
    private readonly AgentryOptions _options = options ?? new AgentryOptions();
    private readonly ILogger _logger = logger ?? NullLogger<AgentRunner<TContext>>.Instance;

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentRequest request, TContext state, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var runId = string.IsNullOrEmpty(request.RunId) ? Guid.NewGuid().ToString("n") : request.RunId!;
        yield return new AgentEvent.Started(runId);

        var history = new List<AgentMessage>(await store.LoadAsync(runId, cancellationToken).ConfigureAwait(false));
        if (!string.IsNullOrEmpty(request.Prompt))
        {
            var userMessage = AgentMessage.User(request.Prompt!);
            history.Add(userMessage);
            await store.AppendAsync(runId, [userMessage], cancellationToken).ConfigureAwait(false);
        }

        var totalUsage = new TokenUsage();
        var tools = executor.Definitions;
        var retries = 0;

        for (var iteration = 0; iteration < request.MaxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var modelRequest = new ModelRequest
            {
                System = request.System,
                Messages = history,
                Tools = tools,
                Model = request.Model,
                MaxTokens = request.MaxTokens,
            };

            var response = await CompleteSafelyAsync(modelRequest, cancellationToken).ConfigureAwait(false);

            if (response.StopReason == StopReason.Error)
            {
                if (retries++ < _options.MaxRetries)
                {
                    _logger.LogWarning("Model error (retry {Retry}): {Error}", retries, response.Error);
                    await Task.Delay(BackoffMs(retries), cancellationToken).ConfigureAwait(false);
                    iteration--; // don't count retries against the iteration budget
                    continue;
                }

                yield return new AgentEvent.Failed(response.Error ?? "model error");
                yield break;
            }

            retries = 0;
            totalUsage += response.Usage;
            if (response.Usage.Total > 0)
                yield return new AgentEvent.UsageUpdated(totalUsage);

            var assistant = AgentMessage.Assistant(response.Text, response.ToolCalls);
            history.Add(assistant);
            await store.AppendAsync(runId, [assistant], cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(response.Text))
                yield return new AgentEvent.AssistantText(response.Text!);

            if (response.StopReason == StopReason.ToolCalls && response.ToolCalls.Count > 0)
            {
                var results = new List<ToolResult>(response.ToolCalls.Count);
                foreach (var call in response.ToolCalls)
                {
                    yield return new AgentEvent.ToolStarted(call.Id, call.Name);
                    var result = await executor.ExecuteAsync(call, state, cancellationToken).ConfigureAwait(false);
                    results.Add(result);
                    yield return new AgentEvent.ToolFinished(call.Id, call.Name, result.IsSuccess);
                }

                var toolMessage = AgentMessage.Tool(results);
                history.Add(toolMessage);
                await store.AppendAsync(runId, [toolMessage], cancellationToken).ConfigureAwait(false);
                continue; // feed results back to the model
            }

            yield return new AgentEvent.Completed(response.StopReason, totalUsage);
            yield break;
        }

        _logger.LogWarning("Run {RunId} hit the {Max}-iteration cap", runId, request.MaxIterations);
        yield return new AgentEvent.Completed(StopReason.MaxTokens, totalUsage);
    }

    private async Task<ModelResponse> CompleteSafelyAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await model.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ModelResponse { StopReason = StopReason.Error, Error = ex.Message };
        }
    }

    private int BackoffMs(int attempt) => _options.RetryBaseDelayMs * (1 << (attempt - 1));
}
