using System.ComponentModel;
using Agentry;
using Microsoft.Extensions.DependencyInjection;

// A tiny "trip planner" agent with one tool.
// Runs OFFLINE by default (a scripted model). Set ANTHROPIC_API_KEY to run it live.

var services = new ServiceCollection();
services.AddAgentry<TripContext>(a => a.AddTool<AddStopTool>());

if (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } apiKey)
{
    services.AddAnthropicChatModel(o => o.ApiKey = apiKey);
    Console.WriteLine("(using live Anthropic)\n");
}
else
{
    services.AddSingleton<IChatModel>(new ScriptedModel());
    Console.WriteLine("(no ANTHROPIC_API_KEY — using offline scripted model)\n");
}

await using var provider = services.BuildServiceProvider();
var runner = provider.GetRequiredService<IAgentRunner<TripContext>>();

var trip = new TripContext();
var request = new AgentRequest
{
    System = "You plan trips by calling add_stop for each city, then summarising.",
    Prompt = "Plan a short trip through Italy.",
};

await foreach (var e in runner.RunAsync(request, trip))
{
    Console.WriteLine(e switch
    {
        AgentEvent.Started s => $">> run {s.RunId}",
        AgentEvent.ToolStarted t => $"   -> {t.ToolName}",
        AgentEvent.ToolFinished t => $"   <- {t.ToolName} [{(t.Success ? "ok" : "fail")}]",
        AgentEvent.AssistantText a => $"   :  {a.Text}",
        AgentEvent.Completed c => $"== done ({c.Reason}, {c.TotalUsage.Total} tokens)",
        AgentEvent.Failed f => $"!! {f.Error}",
        _ => e.ToString() ?? "",
    });
}

Console.WriteLine($"\nItinerary: {string.Join(" -> ", trip.Stops)}");

// ── Domain ───────────────────────────────────────────────────────────────────

public sealed class TripContext
{
    public List<string> Stops { get; } = [];
}

public sealed class AddStopTool : Tool<AddStopTool.Input, TripContext>
{
    public override string Name => "add_stop";
    public override string Description => "Add a city stop to the trip itinerary.";

    protected override Task<ToolResult> ExecuteAsync(Input input, TripContext ctx, ToolCall call, CancellationToken ct)
    {
        ctx.Stops.Add(input.City);
        return Task.FromResult(ToolResult.Ok(call, $"Added {input.City} to the itinerary."));
    }

    public sealed class Input
    {
        [Description("The city to add as a stop")] public string City { get; set; } = "";
    }
}

/// <summary>Offline demo model: adds two stops, then finishes. Decided by tool-result count.</summary>
public sealed class ScriptedModel : IChatModel
{
    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        var stops = request.Messages.SelectMany(m => m.ToolResults).Count();
        ModelResponse response = stops switch
        {
            0 => Call("Rome"),
            1 => Call("Florence"),
            _ => new ModelResponse { StopReason = StopReason.EndTurn, Text = "Trip planned: Rome then Florence.", Usage = new TokenUsage(4, 6) },
        };
        return Task.FromResult(response);

        static ModelResponse Call(string city) => new()
        {
            StopReason = StopReason.ToolCalls,
            ToolCalls = [new ToolCall($"call_{city}", "add_stop", System.Text.Json.JsonSerializer.SerializeToElement(new { city }))],
            Usage = new TokenUsage(12, 8),
        };
    }
}
