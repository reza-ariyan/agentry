# Providers

A provider is an `IChatModel` — the seam between Agentry's loop and a specific model backend. The loop
never talks to a vendor SDK; it talks to this one interface:

```csharp
public interface IChatModel
{
    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default);
}
```

One call = one model turn. Implement it once per backend and the entire loop, every tool, and all the
samples work unchanged.

## What you receive and return

**`ModelRequest`** (in):

| Field | Type | Notes |
|---|---|---|
| `System` | `string?` | System prompt / instructions. |
| `Messages` | `IReadOnlyList<AgentMessage>` | The conversation so far, in order. |
| `Tools` | `IReadOnlyList<ToolDefinition>` | Tools the model may call (`Name`, `Description`, `InputSchema`). |
| `Model` | `string?` | Model id; null → pick your default. |
| `MaxTokens` | `int` | Max tokens to generate. |

**`ModelResponse`** (out):

| Field | Type | Notes |
|---|---|---|
| `Text` | `string?` | Assistant text this turn, if any. |
| `ToolCalls` | `IReadOnlyList<ToolCall>` | Tool calls the model requested (`Id`, `Name`, JSON `Arguments`). |
| `StopReason` | `StopReason` | `ToolCalls`, `EndTurn`, `MaxTokens`, or `Error`. |
| `Usage` | `TokenUsage` | Input/output tokens for this call. |
| `Error` | `string?` | Set when `StopReason == Error`. |
| `IsRetryable` | `bool` | Set `true` for transient errors (see below). |

## The Anthropic provider

`Agentry.Anthropic` is a dependency-free adapter over the Anthropic Messages API (raw `HttpClient`, no
vendor SDK). Register it with `IHttpClientFactory` wiring done for you:

```csharp
services.AddAnthropicChatModel(o =>
{
    o.ApiKey  = config["Anthropic:Key"]!;     // required — throws ArgumentException early if empty
    o.Model   = "claude-haiku-4-5";            // model ids change/retire; set one explicitly
    o.Version = "2023-06-01";                  // anthropic-version header (default shown)
    o.BaseUrl = "https://api.anthropic.com/";  // override for a proxy / gateway
});
```

| `AnthropicOptions` | Default | Notes |
|---|---|---|
| `ApiKey` | `""` | **Required.** Registration throws if empty — fail fast, not at first call. |
| `Model` | `AnthropicOptions.DefaultModel` | The fallback when a request doesn't set `Model`. Set explicitly for production. |
| `Version` | `2023-06-01` | `anthropic-version` header. |
| `BaseUrl` | `https://api.anthropic.com/` | Point at a gateway/proxy if needed. |

It maps Agentry messages to Anthropic content blocks (assistant `text` + `tool_use`, `tool_result`
back as a user turn), parses `usage`, translates `stop_reason`, and classifies transient failures
(HTTP `408/409/429/5xx/529` and network errors) as retryable so the loop backs off and retries.

> Construct it directly (no DI) with `new AnthropicChatModel(httpClient, options)` — handy in tests or
> minimal apps.

## Writing your own provider

Implement `CompleteAsync`: translate `request` into your API's call, then translate the reply back.

```csharp
public sealed class MyChatModel(HttpClient http, MyOptions options) : IChatModel
{
    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        try
        {
            var wire = MapRequest(request);                 // system, messages, tools, max_tokens
            using var resp = await http.PostAsJsonAsync("...", wire, ct);

            if (!resp.IsSuccessStatusCode)
                return new ModelResponse
                {
                    StopReason  = StopReason.Error,
                    Error       = $"HTTP {(int)resp.StatusCode}",
                    IsRetryable = IsTransient(resp.StatusCode),   // 408/409/429/5xx → true
                };

            var body = await resp.Content.ReadFromJsonAsync<MyWireResponse>(ct);
            return new ModelResponse
            {
                Text       = body.Text,
                ToolCalls  = body.ToolCalls.Select(t => new ToolCall(t.Id, t.Name, t.Args)).ToList(),
                StopReason = body.WantsTools ? StopReason.ToolCalls : StopReason.EndTurn,
                Usage      = new TokenUsage(body.InputTokens, body.OutputTokens),
            };
        }
        catch (HttpRequestException ex)
        {
            return new ModelResponse { StopReason = StopReason.Error, Error = ex.Message, IsRetryable = true };
        }
    }
}
```

### Mapping the messages

Translate each `AgentMessage` by `Role`:

| `MessageRole` | Maps to | Carries |
|---|---|---|
| `System` | usually a top-level system field (not a message) | `Text` |
| `User` | a user message | `Text` |
| `Assistant` | an assistant message | `Text` and/or `ToolCalls` (`Id`, `Name`, `Arguments`) |
| `Tool` | a tool-result message (often a "user" turn in chat APIs) | `ToolResults` (`CallId`, `Content`, `IsSuccess`) |

`ToolDefinition.InputSchema` is already a JSON Schema object — pass it straight through as the tool's
parameter schema.

### Contract checklist

- **Set `StopReason` correctly.** `ToolCalls` when the model wants tools, `EndTurn` when it's done,
  `MaxTokens` when truncated. The loop relies on this to decide whether to run tools or stop.
- **Return tool-call ids.** The `ToolCall.Id` you return must be the same id your API expects on the
  tool result next turn — Agentry round-trips it via `ToolResult.CallId`.
- **Classify errors.** Transient (rate limit, 5xx, network) → `StopReason.Error` + `IsRetryable = true`.
  Permanent (bad key, malformed request) → `IsRetryable = false` so the run fails fast.
- **Be defensive when parsing.** Skip malformed content blocks rather than throwing; the loop treats an
  uncaught exception as a non-retryable error.
- **Empty content is fine.** If a turn has neither text nor tool calls, the loop completes cleanly — you
  don't need to synthesize placeholder text.

### Register it

```csharp
services.AddHttpClient<IChatModel, MyChatModel>();   // typed client, like the Anthropic adapter
// or, no HTTP:
services.AddSingleton<IChatModel>(new MyChatModel(...));
```

A provider is also the natural place for a **test double** — return scripted `ModelResponse`s to drive
the loop deterministically in unit tests (see the fakes in `tests/Agentry.Tests`).
