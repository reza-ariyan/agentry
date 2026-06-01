using System.Net;
using System.Text;
using System.Text.Json;

namespace Agentry;

/// <summary>
/// An <see cref="IChatModel"/> implemented directly against the Anthropic Messages API
/// (<c>POST /v1/messages</c>) with a plain <see cref="HttpClient"/> — no vendor SDK.
/// Maps Agentry's neutral messages/tools to/from Anthropic's content-block wire format.
/// </summary>
public sealed class AnthropicChatModel : IChatModel
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly AnthropicOptions _options;

    /// <summary>
    /// Create the adapter. The <paramref name="httpClient"/> is supplied by <c>IHttpClientFactory</c>
    /// when registered via <c>AddAnthropicChatModel</c>; pass your own for direct/no-DI use or tests.
    /// </summary>
    public AnthropicChatModel(HttpClient httpClient, AnthropicOptions options)
    {
        _http = httpClient;
        _options = options;
        _http.BaseAddress ??= new Uri(_options.BaseUrl);
    }

    /// <inheritdoc />
    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model ?? _options.Model,
            ["max_tokens"] = request.MaxTokens,
            ["messages"] = request.Messages.Select(ToWire).Where(m => m is not null).ToList(),
        };
        if (!string.IsNullOrEmpty(request.System))
            body["system"] = request.System;
        if (request.Tools.Count > 0)
            body["tools"] = request.Tools
                .Select(t => new { name = t.Name, description = t.Description, input_schema = t.InputSchema })
                .ToList();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Add("x-api-key", _options.ApiKey);
        httpRequest.Headers.Add("anthropic-version", _options.Version);

        HttpResponseMessage httpResponse;
        string payload;
        try
        {
            httpResponse = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            payload = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new ModelResponse { StopReason = StopReason.Error, Error = $"network error: {ex.Message}", IsRetryable = true };
        }

        using (httpResponse)
        {
            if (!httpResponse.IsSuccessStatusCode)
                return new ModelResponse
                {
                    StopReason = StopReason.Error,
                    Error = $"HTTP {(int)httpResponse.StatusCode}: {Truncate(payload)}",
                    IsRetryable = IsTransient(httpResponse.StatusCode),
                };

            return Parse(payload);
        }
    }

    private static bool IsTransient(HttpStatusCode status) => (int)status switch
    {
        408 or 409 or 429 or 500 or 502 or 503 or 504 or 529 => true,
        _ => false,
    };

    private static object? ToWire(AgentMessage message)
    {
        switch (message.Role)
        {
            case MessageRole.User:
                return new { role = "user", content = new object[] { new { type = "text", text = NonEmpty(message.Text) } } };

            case MessageRole.Assistant:
                var blocks = new List<object>();
                if (!string.IsNullOrEmpty(message.Text))
                    blocks.Add(new { type = "text", text = message.Text });
                foreach (var call in message.ToolCalls)
                    blocks.Add(new { type = "tool_use", id = call.Id, name = call.Name, input = call.Arguments });
                if (blocks.Count == 0) // Anthropic rejects empty content arrays — emit a minimal placeholder.
                    blocks.Add(new { type = "text", text = "(no content)" });
                return new { role = "assistant", content = blocks };

            case MessageRole.Tool:
                if (message.ToolResults.Count == 0)
                    return null; // nothing to send for an empty tool turn
                var results = message.ToolResults
                    .Select(r => (object)new { type = "tool_result", tool_use_id = r.CallId, content = NonEmpty(r.Content), is_error = !r.IsSuccess })
                    .ToList();
                return new { role = "user", content = results };

            default:
                return null; // System is sent via the top-level "system" field, not as a message.
        }
    }

    private static string NonEmpty(string? s) => string.IsNullOrEmpty(s) ? " " : s;

    private static ModelResponse Parse(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var text = new StringBuilder();
        var toolCalls = new List<ToolCall>();

        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetString();

                if (type == "text" && block.TryGetProperty("text", out var t))
                {
                    text.Append(t.GetString());
                }
                else if (type == "tool_use"
                         && block.TryGetProperty("id", out var id)
                         && block.TryGetProperty("name", out var name)
                         && block.TryGetProperty("input", out var input))
                {
                    toolCalls.Add(new ToolCall(id.GetString()!, name.GetString()!, input.Clone()));
                }
            }
        }

        var usage = new TokenUsage();
        if (root.TryGetProperty("usage", out var u))
            usage = new TokenUsage(
                u.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0,
                u.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0);

        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;

        return new ModelResponse
        {
            Text = text.Length > 0 ? text.ToString() : null,
            ToolCalls = toolCalls,
            StopReason = stopReason switch
            {
                "tool_use" => StopReason.ToolCalls,
                "max_tokens" => StopReason.MaxTokens,
                _ => StopReason.EndTurn,
            },
            Usage = usage,
        };
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}
