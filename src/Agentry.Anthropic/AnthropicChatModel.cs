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

    public AnthropicChatModel(AnthropicOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        _http = httpClient ?? new HttpClient();
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

        using var httpResponse = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var payload = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!httpResponse.IsSuccessStatusCode)
            return new ModelResponse { StopReason = StopReason.Error, Error = $"HTTP {(int)httpResponse.StatusCode}: {Truncate(payload)}" };

        return Parse(payload);
    }

    private static object? ToWire(AgentMessage message)
    {
        switch (message.Role)
        {
            case MessageRole.User:
                return new { role = "user", content = new object[] { new { type = "text", text = message.Text ?? "" } } };

            case MessageRole.Assistant:
                var blocks = new List<object>();
                if (!string.IsNullOrEmpty(message.Text))
                    blocks.Add(new { type = "text", text = message.Text });
                foreach (var call in message.ToolCalls)
                    blocks.Add(new { type = "tool_use", id = call.Id, name = call.Name, input = call.Arguments });
                return new { role = "assistant", content = blocks };

            case MessageRole.Tool:
                var results = message.ToolResults
                    .Select(r => (object)new { type = "tool_result", tool_use_id = r.CallId, content = r.Content, is_error = !r.IsSuccess })
                    .ToList();
                return new { role = "user", content = results };

            default:
                return null; // System is sent via the top-level "system" field, not as a message.
        }
    }

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
                var type = block.GetProperty("type").GetString();
                if (type == "text")
                    text.Append(block.GetProperty("text").GetString());
                else if (type == "tool_use")
                    toolCalls.Add(new ToolCall(
                        block.GetProperty("id").GetString()!,
                        block.GetProperty("name").GetString()!,
                        block.GetProperty("input").Clone()));
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
