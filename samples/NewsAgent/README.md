# NewsAgent — a database-backed content agent

An interactive agent that **manages a news database** — create, list, fetch, update,
**improve SEO**, and delete articles — using a real LLM (Anthropic) and **EF Core SQLite**.
A full example; you only set config. It seeds a couple of articles so it works immediately.

## Configure

- **Required:** `ANTHROPIC_API_KEY` (env var).
- **Optional** (`appsettings.json`):
  - `Agent:Model` — Anthropic model id (*model ids change — set a current one*).
  - `Agent:DbPath` — SQLite file path (default: `newsagent.db`, created automatically).

## Run

```bash
ANTHROPIC_API_KEY=sk-ant-... dotnet run --project samples/NewsAgent
```

Then try:

- `list the news`
- `add a news article titled "Agentry adds OpenAI support" about ...`
- `improve the SEO of article 1`  ← the agent reads it, writes meta title/description/slug/keywords, and saves them
- `update article 2's category to "product"`
- `delete article 2`

## Tools

| Tool | Does |
|---|---|
| `create_news` | Insert an article (title, body, category) |
| `list_news` | List articles (optional title filter) |
| `get_news` | Fetch one article + its SEO fields |
| `update_news` | Update title / body / category |
| `set_seo` | Persist SEO the **LLM generated** (meta title, description, slug, keywords) |
| `delete_news` | Delete an article |

**The SEO pattern:** the *agent* writes the SEO copy (that's the LLM's job); the `set_seo`
*tool* just persists it. That split — model reasons, tools act — is the whole idea.

This is the template for an agent over **your** data: swap the SQLite `DbContext` for your
real database (Postgres, SQL Server, ...) and keep the same tools.
