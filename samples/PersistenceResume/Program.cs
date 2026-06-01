// Agentry — persist a conversation and resume it.
// Runs offline (no API key). Demonstrates that a fresh runner over the SAME store
// + the SAME runId picks up the prior conversation history.

using Agentry;

// In real apps, swap this for your own IConversationStore (EF Core, Redis, Mongo, ...).
var store = new InMemoryConversationStore();
const string runId = "conversation-1";

Console.WriteLine("=== session 1 ===");
await RunOnce(store, runId, "Remember that my name is Reza.");

Console.WriteLine("\n--- imagine the process restarts here (new runner, same store) ---\n");

Console.WriteLine("=== session 2 (resumed) ===");
await RunOnce(store, runId, "How many messages have I sent in this conversation?");

var history = await store.LoadAsync(runId);
Console.WriteLine($"\nStored messages in '{runId}': {history.Count}");


// A fresh runner each call — only the shared `store` + `runId` carry state across them.
static async Task RunOnce(IConversationStore store, string runId, string prompt)
{
    var executor = new ToolExecutor<object?>([]);            // no tools needed for this demo
    var runner = new AgentRunner<object?>(new CountingModel(), executor, store);

    await foreach (var e in runner.RunAsync(new AgentRequest { RunId = runId, Prompt = prompt }, state: null))
    {
        if (e is AgentEvent.AssistantText t)
            Console.WriteLine($"assistant: {t.Text}");
    }
}

// A trivial model that reports how many user messages it can see.
// On session 2 it sees BOTH user messages — proof the history was loaded from the store.
public sealed class CountingModel : IChatModel
{
    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        var userMessages = request.Messages.Count(m => m.Role == MessageRole.User);
        return Task.FromResult(new ModelResponse
        {
            StopReason = StopReason.EndTurn,
            Text = $"I can see {userMessages} message(s) in this conversation so far.",
        });
    }
}
