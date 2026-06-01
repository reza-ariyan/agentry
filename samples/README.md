# Agentry samples

Each sample is a tiny, self-contained console app. Most run **offline with no API key** —
just `dotnet run` and watch the agent loop work.

| Sample | What it shows | API key? |
|---|---|---|
| [**NewsAgent**](NewsAgent) | **Real, DB-backed app** — manage a news database (create / list / update / improve SEO / delete) via a real LLM + EF Core SQLite | **Yes** |
| [**FileQaAgent**](FileQaAgent) | **A real, full app** — chat with a folder of files using a real LLM + real file tools. Just set config. | **Yes** |
| [MinimalAgent](MinimalAgent) | The smallest possible agent — one tool, no DI, runs offline | No |
| [PersistenceResume](PersistenceResume) | Persist a conversation and resume it after a "restart" | No |
| [Agentry.Sample](Agentry.Sample) | Tools + streaming progress events; offline by default, or live via Anthropic | Optional |

## Run them

```bash
dotnet run --project samples/MinimalAgent
dotnet run --project samples/PersistenceResume
dotnet run --project samples/Agentry.Sample                                   # offline
ANTHROPIC_API_KEY=sk-ant-... dotnet run --project samples/Agentry.Sample      # live
```

## The 30-second version

```csharp
// 1. register your tools, 2. pick a model, 3. run — and stream every step.
var executor = new ToolExecutor<MyState>([new MyTool()]);
var runner   = new AgentRunner<MyState>(model, executor, new InMemoryConversationStore());

await foreach (var ev in runner.RunAsync(new AgentRequest { Prompt = "..." }, new MyState()))
    Console.WriteLine(ev);   // Started · ToolStarted · ToolFinished · AssistantText · Completed
```

See **[MinimalAgent/Program.cs](MinimalAgent/Program.cs)** for the full, runnable version
(including how to define a tool with an auto-generated schema).
