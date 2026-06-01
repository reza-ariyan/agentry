# Writing a tool

A tool is a capability the model can call. You write the C# method; Agentry generates the JSON schema,
deserializes the model's arguments, dispatches the call, and feeds the result back into the loop.

## The easy way: `Tool<TInput, TContext>`

Derive from `Tool<TInput, TContext>`, declare a nested `Input` class, and implement one method. The
schema is generated from `Input` and the model's arguments arrive already deserialized and typed.

```csharp
using System.ComponentModel;
using Agentry;

public sealed class CreateNewsTool : Tool<CreateNewsTool.Input, NewsAgentContext>
{
    public override string Name => "create_news";
    public override string Description => "Insert a new news article. Returns its id.";

    protected override async Task<ToolResult> ExecuteAsync(
        Input input, NewsAgentContext ctx, ToolCall call, CancellationToken ct)
    {
        await using var db = ctx.NewDb();
        var n = new News { Title = input.Title, Body = input.Body };
        db.News.Add(n);
        await db.SaveChangesAsync(ct);

        return ToolResult.Ok(call, $"Created news #{n.Id}.", new { id = n.Id });
    }

    public sealed class Input
    {
        [Description("Headline / title")] public string Title { get; set; } = "";
        [Description("Article body text")] public string Body { get; set; } = "";
        [Description("Optional category, e.g. release, engineering")] public string? Category { get; set; }
    }
}
```

The four parameters of `ExecuteAsync`:

- **`input`** — your `Input`, deserialized from the model's JSON arguments.
- **`ctx`** — the per-run `TContext` (shared across every tool call in the run).
- **`call`** — the raw `ToolCall` (`Id`, `Name`, `Arguments`). You need it to build the result.
- **`ct`** — cancellation, honored by the loop.

## Returning a result

Always return a `ToolResult` correlated to the `call`:

```csharp
return ToolResult.Ok(call, "Human/model-readable summary", new { id = 42 }); // success
return ToolResult.Fail(call, "No article with id 7.");                       // failure
```

- `Content` (the second arg) is the text the **model** sees on the next turn. Write it for the model:
  state what happened and include ids it will need.
- `Data` (optional) is structured data for **your** app — it's attached to the result but not sent to
  the model verbatim. Use it to capture created ids, etc.
- **Don't throw for expected failures.** Return `Fail(...)`. The loop feeds the message back to the
  model, which can apologize, retry differently, or ask the user. (If your tool *does* throw, the
  executor catches it and turns it into a failed result anyway — but a clear `Fail` message is better
  than an exception string.)

## Schema generation rules

`ToolSchema` builds a JSON Schema from your `Input` type using reflection — no third-party dependency.
Property names are **camelCased** (`MetaTitle` → `metaTitle`); override with `[JsonPropertyName]`.

| C# type | JSON Schema |
|---|---|
| `string` | `{ "type": "string" }` |
| `bool` | `{ "type": "boolean" }` |
| `int` / `long` / `short` / `byte` | `{ "type": "integer" }` |
| `double` / `float` / `decimal` | `{ "type": "number" }` |
| `Guid` | `{ "type": "string", "format": "uuid" }` |
| `DateTime` / `DateTimeOffset` | `{ "type": "string", "format": "date-time" }` |
| `enum` | `{ "type": "string", "enum": [ ...names ] }` |
| nested `class` | `{ "type": "object", ... }` (recursive) |
| `T[]`, `List<T>`, any `IEnumerable<T>` | `{ "type": "array", "items": ... }` |
| `Dictionary<string,T>` / `IReadOnlyDictionary<string,T>` | `{ "type": "object", "additionalProperties": ... }` |

`[Description]` on a property becomes the schema `description` — **use it generously**; it's how the
model understands each field. Recursive/cyclic types are guarded (a back-reference emits a bare
object), so self-referential inputs won't loop forever.

### Required vs. optional

A property is **required** when:

- it's marked with the C# `required` keyword, **or**
- it's a value type (`int`, `bool`, `DateTime`, an `enum`, …), **or**
- it's a non-nullable reference type (`string`, a class).

A property is **optional** when it's nullable — `string?`, `int?`, `MyClass?`. So the simplest way to
make an argument optional is to make it nullable:

```csharp
public sealed class Input
{
    [Description("Article id")] public int Id { get; set; }            // required (value type)
    [Description("New title")] public string? Title { get; set; }      // optional (nullable)
    [Description("New body")]  public string? Body { get; set; }       // optional (nullable)
}
```

Enable nullable reference types in your project (`<Nullable>enable</Nullable>`) so this distinction is
accurate.

### Argument deserialization

Arguments deserialize with web defaults: **camelCase**, **case-insensitive**, **enums as strings**. If
the model omits arguments entirely (or sends `null`), the base class treats it as an empty object `{}`,
so a tool whose inputs are all optional still runs. If the JSON can't be deserialized to your `Input`,
the tool returns a `Fail` with the parse error instead of throwing.

## Naming rules

Tool names must match `^[a-zA-Z0-9_-]{1,64}$` (a provider requirement) and be **unique within an
agent** (case-insensitive). The `ToolExecutor` validates this when it's constructed and throws a clear
`InvalidOperationException` for an invalid or duplicate name — you find out at startup, not mid-run.
Use `snake_case` names like `create_news`, `list_files`.

## Dependencies & state

Tools are resolved from DI when you register them by type, so they can take constructor dependencies:

```csharp
public sealed class CreateUserTool(IUserService users) : Tool<CreateUserTool.Input, MyContext>
{
    // `users` is injected by the container
}

services.AddAgentry<MyContext>(a => a.AddTool<CreateUserTool>());
```

Use **constructor injection** for app services (repositories, HTTP clients, the current request's
scope) and **`TContext`** for per-run state shared across tool calls (ids created so far, a DB
factory, the active tenant). Register a pre-built instance instead with `a.AddTool(new MyTool())` when
it has no dependencies.

## The raw interface

For full control (a hand-written schema, a dynamic tool), implement `ITool<TContext>` directly:

```csharp
public sealed class PingTool : ITool<MyContext>
{
    public string Name => "ping";
    public string Description => "Returns pong.";
    public object InputSchema { get; } = new Dictionary<string, object> { ["type"] = "object" };

    public Task<ToolResult> ExecuteAsync(ToolCall call, MyContext ctx, CancellationToken ct = default)
        => Task.FromResult(ToolResult.Ok(call, "pong"));
}
```

`Tool<TInput,TContext>` is just a convenience layer over this — it generates `InputSchema` and
deserializes `call.Arguments` for you.

## Checklist for a good tool

- **Name** is `snake_case`, specific, and verb-led (`improve_seo`, not `seo`).
- **Description** tells the model *when* to use it and any important constraints.
- Every `Input` property has a `[Description]`; optional ones are nullable.
- Returns `ToolResult.Ok` with a model-readable summary (include ids), `ToolResult.Fail` with an
  actionable message on expected failures.
- Does one thing. Compose multiple small tools rather than one tool with a `mode` switch.
- Validates beyond the schema where needed and `Fail`s with a clear message instead of throwing.

See the **[NewsAgent](../samples/NewsAgent)** sample for six real database-backed tools, and
**[FileQaAgent](../samples/FileQaAgent)** for read-only filesystem tools.
