using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentry;

/// <summary>Runs an agent: the model ↔ tool loop, streaming <see cref="AgentEvent"/>s as it goes.</summary>
public interface IAgentRunner<TContext>
{
    /// <summary>
    /// Run the agent, yielding progress events as they happen. Resumes prior history if
    /// <see cref="AgentRequest.RunId"/> exists in the store. Drain the stream fully (or cancel)
    /// to avoid leaving a half-written turn in the store. For a simple final result, prefer
    /// <see cref="AgentRunnerExtensions.RunToCompletionAsync{TContext}"/>.
    /// </summary>
    IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, TContext state, CancellationToken cancellationToken = default);
}

/// <summary>Tuning knobs for the agent loop.</summary>
public sealed class AgentryOptions
{
    /// <summary>Max consecutive transient-error retries before failing the run.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base backoff in ms; doubles each retry (1s, 2s, 4s, …), capped.</summary>
    public int RetryBaseDelayMs { get; set; } = 1000;
}

/// <summary>One agent run's inputs (the per-run state is passed separately and typed).</summary>
public sealed record AgentRequest
{
    /// <summary>Stable id for persistence + resume (same value as the store's conversation id). Defaults to a new id per run.</summary>
    public string? RunId { get; init; }

    /// <summary>System prompt / instructions.</summary>
    public string? System { get; init; }

    /// <summary>The user message that starts (or continues) the run. Optional when resuming.</summary>
    public string? Prompt { get; init; }

    /// <summary>Provider model id; null lets the adapter choose its default.</summary>
    public string? Model { get; init; }

    /// <summary>Max tokens per model call.</summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>Hard cap on loop iterations (safety against runaway agents). Must be ≥ 1.</summary>
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
        if (request.MaxIterations < 1)
            throw new ArgumentOutOfRangeException(nameof(request), "MaxIterations must be at least 1.");

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
        var iteration = 0;
        var retries = 0;

        while (iteration < request.MaxIterations)
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
                if (response.IsRetryable && retries < _options.MaxRetries)
                {
                    retries++;
                    _logger.LogWarning("Transient model error (retry {Retry}/{Max}): {Error}", retries, _options.MaxRetries, response.Error);
                    await Task.Delay(BackoffMs(retries), cancellationToken).ConfigureAwait(false);
                    continue; // retry — does not consume an iteration
                }

                yield return new AgentEvent.Failed(response.Error ?? "model error");
                yield break;
            }

            retries = 0;
            iteration++;
            totalUsage += response.Usage;
            if (response.Usage.Total > 0)
                yield return new AgentEvent.UsageUpdated(totalUsage);

            var hasTools = response.StopReason == StopReason.ToolCalls && response.ToolCalls.Count > 0;
            var hasText = !string.IsNullOrEmpty(response.Text);

            // A contentless turn (no text, no tool calls) is nothing to persist or replay — treat as done.
            if (!hasTools && !hasText)
            {
                yield return new AgentEvent.Completed(StopReason.EndTurn, totalUsage);
                yield break;
            }

            var assistant = AgentMessage.Assistant(response.Text, response.ToolCalls);
            history.Add(assistant);
            await store.AppendAsync(runId, [assistant], cancellationToken).ConfigureAwait(false);

            if (hasText)
                yield return new AgentEvent.AssistantText(response.Text!);

            if (hasTools)
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
                continue; // feed the results back to the model
            }

            yield return new AgentEvent.Completed(response.StopReason, totalUsage);
            yield break;
        }

        _logger.LogWarning("Run {RunId} hit the {Max}-iteration cap", runId, request.MaxIterations);
        yield return new AgentEvent.Completed(StopReason.MaxIterations, totalUsage);
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
            // Unknown exceptions are treated as non-retryable by default (the adapter sets IsRetryable for transient ones).
            return new ModelResponse { StopReason = StopReason.Error, Error = ex.Message };
        }
    }

    private int BackoffMs(int attempt) => _options.RetryBaseDelayMs * (1 << Math.Min(attempt - 1, 10));
}

/// <summary>The outcome of running an agent to completion.</summary>
public sealed record AgentResult
{
    /// <summary>The run/conversation id (use it to resume later).</summary>
    public required string RunId { get; init; }

    /// <summary>The final assistant text (the last <see cref="AgentEvent.AssistantText"/> emitted), or null.</summary>
    public string? Text { get; init; }

    /// <summary>Why the run ended.</summary>
    public StopReason StopReason { get; init; }

    /// <summary>Cumulative token usage across the whole run.</summary>
    public TokenUsage Usage { get; init; }

    /// <summary>Error detail when the run failed.</summary>
    public string? Error { get; init; }

    /// <summary>True when the run completed without failing.</summary>
    public bool IsSuccess => Error is null && StopReason != StopReason.Error;
}

/// <summary>Convenience helpers over <see cref="IAgentRunner{TContext}"/>.</summary>
public static class AgentRunnerExtensions
{
    /// <summary>
    /// Run the agent to completion and return the final text + usage, without consuming the event
    /// stream yourself. Use this when you just want the answer; use <see cref="IAgentRunner{TContext}.RunAsync"/>
    /// directly when you want to stream progress (e.g. to an SSE endpoint or UI).
    /// </summary>
    public static async Task<AgentResult> RunToCompletionAsync<TContext>(
        this IAgentRunner<TContext> runner, AgentRequest request, TContext state, CancellationToken cancellationToken = default)
    {
        string? runId = null, lastText = null, error = null;
        var usage = default(TokenUsage);
        var stop = StopReason.EndTurn;

        await foreach (var e in runner.RunAsync(request, state, cancellationToken).ConfigureAwait(false))
        {
            switch (e)
            {
                case AgentEvent.Started s: runId = s.RunId; break;
                case AgentEvent.AssistantText a: lastText = a.Text; break;
                case AgentEvent.UsageUpdated u: usage = u.Cumulative; break;
                case AgentEvent.Completed c: stop = c.Reason; usage = c.TotalUsage; break;
                case AgentEvent.Failed f: error = f.Error; stop = StopReason.Error; break;
            }
        }

        return new AgentResult
        {
            RunId = runId ?? request.RunId ?? "",
            Text = lastText,
            Usage = usage,
            StopReason = stop,
            Error = error,
        };
    }
}
