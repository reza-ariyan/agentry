<h1 align="center">Agentry</h1>

<p align="center">
  <b>A lightweight, provider-agnostic agentic tool-use loop for .NET.</b><br/>
  You define the tools. Agentry runs the model ↔ tool conversation.
</p>

<p align="center">
  <img alt="net8.0 | net10.0" src="https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4">
  <img alt="MIT" src="https://img.shields.io/badge/license-MIT-green">
  <img alt="status: reference implementation" src="https://img.shields.io/badge/status-reference%20implementation-blue">
</p>

---

## Origin

Agentry is a **clean-room reimplementation** of an agentic engine I built (72 tools, an autonomous tool-use loop, streaming, crash-resume) to power a production multi-tenant SaaS — *before* [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) existed. The original is proprietary and tightly coupled to a private platform; Agentry is the generalized, dependency-light version, open-sourced as a **reference implementation** of how an agentic tool-use loop actually works under the hood: the loop, the tool protocol, schema generation, persistence/resume, and a provider seam. See **[docs/ORIGIN.md](docs/ORIGIN.md)** for sanitized excerpts of the original and exactly how Agentry differs.

> **Building production agents today?** Look at Microsoft Agent Framework first — it's the GA, Microsoft-backed standard. Agentry is intentionally tiny: a great way to *understand* the machinery, or to drop a minimal agent into an app without a heavyweight platform.

## What it gives you

- **Tools as typed classes** — implement `ITool<TContext>`; JSON schemas are generated from your C# input types.
- **A typed session state** (`TContext`) threaded through every tool call.
- **An autonomous loop** — model → tool calls → results → repeat, with stop conditions, iteration limits, and retry/backoff.
- **A provider seam** (`IChatModel`) — swap models without touching the loop. Anthropic adapter included.
- **Pluggable persistence + resume** (`IConversationStore`) — in-memory by default; bring your own store.
- **Streamable progress** (`AgentEvent`) — surface tool/assistant/usage events over SSE, logs, anything.

## Packages

| Package | Purpose |
|---|---|
| `Agentry.Abstractions` | Contracts: tools, model seam, messages, events, store |
| `Agentry.Core` | The loop engine, tool executor, schema generation, DI |
| `Agentry.Anthropic` | `IChatModel` over the Anthropic Messages API |

## Quickstart

```csharp
// 1. Your per-run state, threaded through tools
public sealed class MyContext { public List<long> CreatedIds { get; } = []; }

// 2. A tool — schema generated from the input type
public sealed class CreateUserTool(IUserService users) : Tool<CreateUserTool.Input, MyContext>
{
    public override string Name => "create_user";
    public override string Description => "Creates a user account.";

    protected override async Task<ToolResult> ExecuteAsync(Input input, MyContext ctx, ToolCall call, CancellationToken ct)
    {
        var id = await users.CreateAsync(input.Email, ct);
        ctx.CreatedIds.Add(id);
        return ToolResult.Ok(call, $"Created user {id}", new { id });
    }

    public sealed class Input { [Description("Email address")] public string Email { get; set; } = ""; }
}

// 3. Register
services.AddAgentry<MyContext>(a => a.AddTool<CreateUserTool>());
services.AddAnthropicChatModel(o => o.ApiKey = config["Anthropic:Key"]!);

// 4. Run — stream events as the agent works
var runner = provider.GetRequiredService<IAgentRunner<MyContext>>();
await foreach (var ev in runner.RunAsync(
    new AgentRequest { System = "You are a helpful admin assistant.", Prompt = "Create a user for jane@acme.com" },
    new MyContext()))
{
    // ev: Started | AssistantText | ToolStarted | ToolFinished | Completed | Failed
}
```

## Status

Reference implementation / portfolio project. APIs may change. MIT licensed.
