using System.ComponentModel;
using System.Text.Json;
using Agentry;
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

// ── Tests ───────────────────────────────────────────────────────────────────

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
}

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
}

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
}
