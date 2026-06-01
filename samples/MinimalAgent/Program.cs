// Agentry — the smallest possible agent.
// No dependency injection, no API key. Just `dotnet run`.
//
// It runs an agent that uses ONE tool (a calculator) driven by a tiny built-in
// offline model, so you can watch the loop work instantly. To use a real model,
// see the Agentry.Sample (it wires up Anthropic).

using System.ComponentModel;
using System.Text.Json;
using Agentry;

// 1) Your per-run state — anything you want threaded through tool calls.
var state = new CalcState();

// 2) Register your tool(s) behind the executor.
var executor = new ToolExecutor<CalcState>([new AddTool()]);

// 3) Pick a model. Here: a tiny offline model so it runs with no API key.
IChatModel model = new OfflineModel();

// 4) Build the runner and go — stream every step the agent takes.
var runner = new AgentRunner<CalcState>(model, executor, new InMemoryConversationStore());

await foreach (var e in runner.RunAsync(new AgentRequest { Prompt = "What is 21 + 21?" }, state))
{
    Console.WriteLine(e switch
    {
        AgentEvent.ToolStarted t  => $"  -> calling {t.ToolName}",
        AgentEvent.ToolFinished t => $"  <- {t.ToolName} done",
        AgentEvent.AssistantText a => $"  : {a.Text}",
        AgentEvent.Completed       => "done.",
        _ => "",
    });
}

Console.WriteLine($"\nThe tool computed: {state.LastResult}");


// ── State ────────────────────────────────────────────────────────────────────
public sealed class CalcState
{
    public double LastResult { get; set; }
}

// ── A tool. The JSON schema is generated from AddInput automatically. ──────────
public sealed class AddTool : Tool<AddTool.AddInput, CalcState>
{
    public override string Name => "add";
    public override string Description => "Add two numbers and return the sum.";

    protected override Task<ToolResult> ExecuteAsync(AddInput input, CalcState ctx, ToolCall call, CancellationToken ct)
    {
        ctx.LastResult = input.A + input.B;
        return Task.FromResult(ToolResult.Ok(call, $"{input.A} + {input.B} = {ctx.LastResult}"));
    }

    public sealed class AddInput
    {
        [Description("First number")] public double A { get; set; }
        [Description("Second number")] public double B { get; set; }
    }
}

// ── A tiny offline model: call "add" once, then finish. (Swap for a real model.) ─
public sealed class OfflineModel : IChatModel
{
    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        var toolAlreadyRan = request.Messages.Any(m => m.ToolResults.Count > 0);

        ModelResponse response = toolAlreadyRan
            ? new ModelResponse { StopReason = StopReason.EndTurn, Text = "All done — see the result above." }
            : new ModelResponse
            {
                StopReason = StopReason.ToolCalls,
                ToolCalls = [new ToolCall("call-1", "add", JsonSerializer.SerializeToElement(new { a = 21, b = 21 }))],
            };

        return Task.FromResult(response);
    }
}
