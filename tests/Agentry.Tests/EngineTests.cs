using System.ComponentModel;
using System.Text.Json;
using Agentry;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentry.Tests;

// ── Test doubles ────────────────────────────────────────────────────────────

public sealed class TestContext
{
    public List<string> Echoed { get; } = [];
}

public sealed class EchoTool : Tool<EchoTool.Input, TestContext>
{
    public override string Name => "echo";
    public override string Description => "Echoes the supplied text back.";

    protected override Task<ToolResult> ExecuteAsync(Input input, TestContext ctx, ToolCall call, CancellationToken ct)
    {
        ctx.Echoed.Add(input.Text);
        return Task.FromResult(ToolResult.Ok(call, $"echoed: {input.Text}"));
    }

    public sealed class Input
    {
        [Description("The text to echo")] public string Text { get; set; } = "";
        [Description("How many times")] public int Count { get; set; }
        [Description("Optional note")] public string? Note { get; set; }
    }
}

public sealed class ThrowingTool : ITool<TestContext>
{
    public string Name => "boom";
    public string Description => "Always throws.";
    public object InputSchema { get; } = new Dictionary<string, object> { ["type"] = "object" };
    public Task<ToolResult> ExecuteAsync(ToolCall call, TestContext context, CancellationToken ct = default)
        => throw new InvalidOperationException("kaboom");
}

public sealed class BadNameTool : ITool<TestContext>
{
    public string Name => "bad name!"; // spaces + punctuation are not allowed
    public string Description => "Has an invalid name.";
    public object InputSchema { get; } = new Dictionary<string, object> { ["type"] = "object" };
    public Task<ToolResult> ExecuteAsync(ToolCall call, TestContext context, CancellationToken ct = default)
        => Task.FromResult(ToolResult.Ok(call, "ok"));
}

/// <summary>Scripts one tool call then completes — decided by how many tool results are already present.</summary>
public sealed class FakeChatModel : IChatModel
{
    public int Calls { get; private set; }

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        Calls++;
        var priorResults = request.Messages.SelectMany(m => m.ToolResults).Count();
        var response = priorResults == 0
            ? new ModelResponse
            {
                StopReason = StopReason.ToolCalls,
                ToolCalls = [new ToolCall("call_1", "echo", JsonSerializer.SerializeToElement(new { text = "hi", count = 1 }))],
                Usage = new TokenUsage(10, 5),
            }
            : new ModelResponse { StopReason = StopReason.EndTurn, Text = "all done", Usage = new TokenUsage(3, 2) };
        return Task.FromResult(response);
    }
}

/// <summary>Fails <paramref name="failCount"/> times (optionally retryable) then completes.</summary>
public sealed class FlakyModel(int failCount, bool retryable) : IChatModel
{
    public int Calls { get; private set; }

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Calls <= failCount
            ? new ModelResponse { StopReason = StopReason.Error, Error = "boom", IsRetryable = retryable }
            : new ModelResponse { StopReason = StopReason.EndTurn, Text = "recovered", Usage = new TokenUsage(1, 1) });
    }
}

/// <summary>Returns a contentless turn (no text, no tool calls).</summary>
public sealed class EmptyModel : IChatModel
{
    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(new ModelResponse { StopReason = StopReason.EndTurn, Text = null });
}

// ── Schema ────────────────────────────────────────────────────────────────────

public class ToolSchemaTests
{
    [Fact]
    public void Generates_object_schema_with_camelCase_properties()
    {
        var schema = (JsonElement)ToolSchema.For<EchoTool.Input>();

        Assert.Equal("object", schema.GetProperty("type").GetString());
        var props = schema.GetProperty("properties");
        Assert.True(props.TryGetProperty("text", out _));
        Assert.True(props.TryGetProperty("count", out var count));
        Assert.Equal("integer", count.GetProperty("type").GetString());
        Assert.Equal("The text to echo", props.GetProperty("text").GetProperty("description").GetString());
    }

    [Fact]
    public void Non_nullable_properties_are_required_nullable_are_not()
    {
        var schema = (JsonElement)ToolSchema.For<EchoTool.Input>();
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("text", required);   // non-nullable string
        Assert.Contains("count", required);   // value type
        Assert.DoesNotContain("note", required); // nullable string
    }

    [Fact]
    public void Required_member_keyword_marks_property_required()
    {
        var schema = (JsonElement)ToolSchema.For<RequiredHolder>();
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("name", required);
    }

    [Fact]
    public void String_keyed_dictionary_becomes_open_object()
    {
        var schema = (JsonElement)ToolSchema.For<DictHolder>();
        var map = schema.GetProperty("properties").GetProperty("map");
        Assert.Equal("object", map.GetProperty("type").GetString());
        Assert.Equal("integer", map.GetProperty("additionalProperties").GetProperty("type").GetString());
    }

    public sealed class RequiredHolder
    {
        public required string Name { get; set; }
    }

    public sealed class DictHolder
    {
        public Dictionary<string, int> Map { get; set; } = [];
    }
}

// ── Executor ──────────────────────────────────────────────────────────────────

public class ToolExecutorTests
{
    [Fact]
    public async Task Unknown_tool_returns_failure_not_throw()
    {
        var executor = new ToolExecutor<TestContext>([]);
        var result = await executor.ExecuteAsync(new ToolCall("c", "missing", default), new TestContext());
        Assert.False(result.IsSuccess);
        Assert.Contains("missing", result.Content);
    }

    [Fact]
    public async Task Throwing_tool_becomes_failed_result()
    {
        var executor = new ToolExecutor<TestContext>([new ThrowingTool()]);
        var result = await executor.ExecuteAsync(new ToolCall("c", "boom", default), new TestContext());
        Assert.False(result.IsSuccess);
        Assert.Contains("kaboom", result.Content);
    }

    [Fact]
    public void Exposes_definitions_for_registered_tools()
    {
        var executor = new ToolExecutor<TestContext>([new EchoTool()]);
        Assert.Single(executor.Definitions);
        Assert.Equal("echo", executor.Definitions[0].Name);
    }

    [Fact]
    public void Invalid_tool_name_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ToolExecutor<TestContext>([new BadNameTool()]));
        Assert.Contains("Invalid tool name", ex.Message);
    }

    [Fact]
    public void Duplicate_tool_names_throw()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ToolExecutor<TestContext>([new EchoTool(), new EchoTool()]));
        Assert.Contains("Duplicate tool name", ex.Message);
    }
}

// ── Runner ──────────────────────────────────────────────────────────────────

public class AgentRunnerTests
{
    private static AgentRunner<TestContext> NewRunner(IConversationStore store, params ITool<TestContext>[] tools)
        => new(new FakeChatModel(), new ToolExecutor<TestContext>(tools), store);

    [Fact]
    public async Task Runs_loop_invokes_tool_and_completes()
    {
        var store = new InMemoryConversationStore();
        var ctx = new TestContext();
        var runner = NewRunner(store, new EchoTool());

        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(new AgentRequest { RunId = "t1", Prompt = "go" }, ctx))
            events.Add(e);

        Assert.IsType<AgentEvent.Started>(events[0]);
        Assert.Contains(events, e => e is AgentEvent.ToolStarted { ToolName: "echo" });
        Assert.Contains(events, e => e is AgentEvent.ToolFinished { Success: true });
        Assert.Contains(events, e => e is AgentEvent.Completed { Reason: StopReason.EndTurn });
        Assert.Single(ctx.Echoed);
        Assert.Equal("hi", ctx.Echoed[0]);

        var history = await store.LoadAsync("t1");
        Assert.NotEmpty(history);
    }

    [Fact]
    public async Task Accumulates_token_usage()
    {
        var store = new InMemoryConversationStore();
        var runner = NewRunner(store, new EchoTool());

        TokenUsage? last = null;
        await foreach (var e in runner.RunAsync(new AgentRequest { RunId = "t2", Prompt = "go" }, new TestContext()))
            if (e is AgentEvent.Completed c) last = c.TotalUsage;

        Assert.NotNull(last);
        Assert.Equal(20, last!.Value.Total); // (10+5) first turn + (3+2) second turn
    }

    [Fact]
    public async Task Resumes_from_prior_history_without_recalling_tool()
    {
        var store = new InMemoryConversationStore();
        await store.AppendAsync("r1",
        [
            AgentMessage.User("first"),
            AgentMessage.Assistant(null, [new ToolCall("call_1", "echo", JsonSerializer.SerializeToElement(new { text = "x" }))]),
            AgentMessage.Tool([new ToolResult("call_1", true, "echoed: x")]),
        ]);

        var ctx = new TestContext();
        var runner = NewRunner(store, new EchoTool());

        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(new AgentRequest { RunId = "r1", Prompt = "again" }, ctx))
            events.Add(e);

        // The fake sees a prior tool result in loaded history -> completes immediately, no new tool call.
        Assert.DoesNotContain(events, e => e is AgentEvent.ToolStarted);
        Assert.Contains(events, e => e is AgentEvent.Completed);
        Assert.Empty(ctx.Echoed);
    }

    [Fact]
    public async Task Contentless_turn_completes_cleanly()
    {
        var runner = new AgentRunner<TestContext>(new EmptyModel(), new ToolExecutor<TestContext>([]), new InMemoryConversationStore());

        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(new AgentRequest { RunId = "e1", Prompt = "go" }, new TestContext()))
            events.Add(e);

        Assert.Contains(events, e => e is AgentEvent.Completed { Reason: StopReason.EndTurn });
        Assert.DoesNotContain(events, e => e is AgentEvent.AssistantText);
    }

    [Fact]
    public async Task Rejects_zero_iterations()
    {
        var runner = new AgentRunner<TestContext>(new FakeChatModel(), new ToolExecutor<TestContext>([]), new InMemoryConversationStore());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in runner.RunAsync(new AgentRequest { MaxIterations = 0, Prompt = "go" }, new TestContext())) { }
        });
    }
}

// ── Retry classification ──────────────────────────────────────────────────────

public class RetryTests
{
    private static AgentRunner<TestContext> Runner(IChatModel model)
        => new(model, new ToolExecutor<TestContext>([]), new InMemoryConversationStore(),
               new AgentryOptions { MaxRetries = 3, RetryBaseDelayMs = 1 });

    [Fact]
    public async Task Retries_transient_errors_then_succeeds()
    {
        var model = new FlakyModel(failCount: 2, retryable: true);
        var result = await Runner(model).RunToCompletionAsync(new AgentRequest { Prompt = "go" }, new TestContext());

        Assert.True(result.IsSuccess);
        Assert.Equal("recovered", result.Text);
        Assert.Equal(3, model.Calls); // 2 failures + 1 success
    }

    [Fact]
    public async Task Does_not_retry_permanent_errors()
    {
        var model = new FlakyModel(failCount: 1, retryable: false);
        var result = await Runner(model).RunToCompletionAsync(new AgentRequest { Prompt = "go" }, new TestContext());

        Assert.False(result.IsSuccess);
        Assert.Equal(1, model.Calls); // failed immediately, no retry
        Assert.Equal("boom", result.Error);
    }

    [Fact]
    public async Task Gives_up_after_max_retries()
    {
        var model = new FlakyModel(failCount: 99, retryable: true);
        var result = await Runner(model).RunToCompletionAsync(new AgentRequest { Prompt = "go" }, new TestContext());

        Assert.False(result.IsSuccess);
        Assert.Equal(4, model.Calls); // 1 initial + 3 retries
    }
}

// ── RunToCompletion ergonomics ────────────────────────────────────────────────

public class RunToCompletionTests
{
    [Fact]
    public async Task Returns_final_text_usage_and_runid()
    {
        var runner = new AgentRunner<TestContext>(new FakeChatModel(), new ToolExecutor<TestContext>([new EchoTool()]), new InMemoryConversationStore());
        var result = await runner.RunToCompletionAsync(new AgentRequest { RunId = "rc1", Prompt = "go" }, new TestContext());

        Assert.Equal("rc1", result.RunId);
        Assert.Equal("all done", result.Text);
        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.Equal(20, result.Usage.Total);
        Assert.True(result.IsSuccess);
    }
}

// ── DI wiring ─────────────────────────────────────────────────────────────────

public class DependencyInjectionTests
{
    [Fact]
    public void AddTool_instance_is_discovered_by_executor()
    {
        var services = new ServiceCollection();
        services.AddAgentry<TestContext>(a => a.AddTool(new EchoTool()));
        services.AddSingleton<IChatModel>(new FakeChatModel());

        using var provider = services.BuildServiceProvider();
        var executor = provider.GetRequiredService<IToolExecutor<TestContext>>();

        Assert.Contains(executor.Definitions, d => d.Name == "echo");
    }

    [Fact]
    public void Resolves_agent_runner_with_tool_type()
    {
        var services = new ServiceCollection();
        services.AddAgentry<TestContext>(a => a.AddTool<EchoTool>());
        services.AddSingleton<IChatModel>(new FakeChatModel());

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IAgentRunner<TestContext>>());
    }
}
