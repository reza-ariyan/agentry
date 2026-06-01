# Persistence & resume

Agentry persists the conversation as it runs, so an agent can **resume after a process restart** — pick
up a half-finished run exactly where it stopped. Persistence lives behind one interface:

```csharp
public interface IConversationStore
{
    Task AppendAsync(string conversationId, IEnumerable<AgentMessage> messages, CancellationToken ct = default);
    Task<IReadOnlyList<AgentMessage>> LoadAsync(string conversationId, CancellationToken ct = default);
}
```

The loop calls `LoadAsync` once at the start of a run (keyed by `RunId`) and `AppendAsync` after each
new turn.

## What gets persisted

For a run that creates one article, the store accumulates, in order:

1. the **user** message (the prompt),
2. an **assistant** message carrying the model's `tool_use` call(s),
3. a **tool** message carrying the `ToolResult`(s),
4. … repeat 2–3 for each tool round,
5. the final **assistant** message (the model's closing text).

The final *contentless* turn (no text, no tool calls) is **not** persisted — so resuming never replays
an empty turn. Each message is a neutral `AgentMessage` (`Role`, `Text`, `ToolCalls`, `ToolResults`),
which serializes cleanly to JSON.

## The default: in-memory

`AddAgentry<TContext>(...)` registers `InMemoryConversationStore` automatically. It's thread-safe and
zero-config — ideal for tests, single-process apps, and request-scoped runs. It does **not** survive a
restart; for that, bring a durable store.

## Resume, concretely

Resuming is just "run again with the same `RunId`":

```csharp
var runId = "news-42";

// session 1 — starts a fresh conversation
await runner.RunToCompletionAsync(new AgentRequest { RunId = runId, Prompt = "Draft an article" }, state);

// ── process restarts; a NEW runner + the SAME durable store ──

// session 2 — same RunId: prior history is loaded, the agent continues the thread
await runner.RunToCompletionAsync(new AgentRequest { RunId = runId, Prompt = "Now improve its SEO" }, state);
```

The **[PersistenceResume](../samples/PersistenceResume)** sample demonstrates this end to end (offline,
no API key): it runs, throws away the runner, builds a new one over the same store, and shows the
second session seeing the first session's messages.

> `TContext` is **not** persisted — only the conversation is. Rebuild per-run state (DB factories, the
> current user, caches) when you resume; keep durable facts in your own database, referenced from tool
> results.

## Writing a durable store (EF Core)

Persist each `AgentMessage` as a row with an order column. A compact approach stores the message body
as JSON:

```csharp
public sealed class StoredMessage
{
    public long Id { get; set; }                 // autoincrement = natural order
    public string ConversationId { get; set; } = "";
    public string Json { get; set; } = "";        // serialized AgentMessage
}

public sealed class ChatDbContext(DbContextOptions<ChatDbContext> o) : DbContext(o)
{
    public DbSet<StoredMessage> Messages => Set<StoredMessage>();

    protected override void OnModelCreating(ModelBuilder b) =>
        b.Entity<StoredMessage>().HasIndex(m => new { m.ConversationId, m.Id });
}

public sealed class EfConversationStore(IDbContextFactory<ChatDbContext> factory) : IConversationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task AppendAsync(string conversationId, IEnumerable<AgentMessage> messages, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var m in messages)
            db.Messages.Add(new StoredMessage { ConversationId = conversationId, Json = JsonSerializer.Serialize(m, Json) });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AgentMessage>> LoadAsync(string conversationId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Messages
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Id)
            .Select(m => m.Json)
            .ToListAsync(ct);
        return rows.Select(j => JsonSerializer.Deserialize<AgentMessage>(j, Json)!).ToList();
    }
}
```

Register it (overriding the in-memory default):

```csharp
services.AddDbContextFactory<ChatDbContext>(o => o.UseNpgsql(connectionString));
services.AddSingleton<IConversationStore, EfConversationStore>();
services.AddAgentry<MyContext>(a => a.AddTool<...>());   // AddAgentry's TryAdd won't replace your store
```

`AddAgentry` registers the in-memory store with `TryAddSingleton`, so a store you register **before or
after** wins — there's no need to remove anything.

## Implementation notes

- **Ordering is the contract.** `LoadAsync` must return messages in append order. An autoincrement key
  (or an explicit sequence column) is the simplest way to guarantee it.
- **Append is additive.** The loop only ever appends; it never updates or deletes. You can treat the
  table as an append-only log.
- **One writer per run.** A single run appends sequentially. If you might run the *same* `RunId`
  concurrently (usually a mistake), add your own guard — Agentry doesn't lock across stores.
- **Storing JSON vs. columns.** JSON (above) is simplest and round-trips `AgentMessage` exactly.
  Prefer normalized columns only if you need to query message contents in SQL.
- **Serialization.** `AgentMessage` is a plain record with list properties; `System.Text.Json` handles
  it with no custom converters. `ToolCall.Arguments` is a `JsonElement` and serializes as-is.
