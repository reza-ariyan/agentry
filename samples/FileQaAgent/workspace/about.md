# About this workspace

This folder is sample content for the FileQaAgent. Point the agent at any folder of
your own (docs, a codebase, notes) by changing `Agent:Folder` in appsettings.json.

## What is Agentry?

Agentry is a lightweight, provider-agnostic **agentic tool-use loop for .NET**. You
define tools (classes implementing `ITool<TContext>`), pick a model (any `IChatModel`),
and the engine runs the model-and-tool conversation for you — with persistence and
streaming.

## Key building blocks

- **ToolExecutor<TContext>** — dispatches a tool call to the right tool; never throws.
- **AgentRunner<TContext>** — runs the loop: model -> tool calls -> results -> repeat,
  until the model ends the turn. Tracks token usage and stop reasons.
- **IConversationStore** — persists messages so a run can resume after a restart.
- **AgentEvent** — the stream of progress events (started, tool started/finished,
  assistant text, completed).
