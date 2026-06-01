# FileQaAgent — a real, runnable agent

An interactive agent that **answers questions about a folder of files**, using a real
LLM (Anthropic) and real filesystem tools. This is a full example — you only set config.

## Configure

- **Required:** `ANTHROPIC_API_KEY` (your Anthropic API key) — set as an environment variable.
- **Optional** (`appsettings.json`):
  - `Agent:Model` — Anthropic model id. *Model ids change over time — set a current one.*
  - `Agent:Folder` — folder to search (default: `workspace`, included so it runs out of the box).

## Run

```bash
ANTHROPIC_API_KEY=sk-ant-... dotnet run --project samples/FileQaAgent
```

Then ask things like:

- `What is Agentry?`
- `Where is the loop implemented?`
- `Summarize the architecture notes.`

Point it at **your own** folder by setting `Agent:Folder` (e.g. a docs or source directory),
then ask questions about it.

## How it works

- **Tools:** `list_files`, `search_files`, `read_file` — real filesystem access, safely
  scoped to the configured folder (no path traversal).
- The agent calls these to ground every answer in the actual files, and cites what it read.
- The conversation **persists in-memory across your questions**, so it's a real multi-turn chat.

This is the pattern for any "agent that works with your data": swap the file tools for
tools that hit your API, your database, or your services.
