using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace IntellectHub.Client;

public sealed class ChatMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public JsonNode? Content { get; set; }
}

public sealed class ToolSpec
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("input_schema")] public JsonNode InputSchema { get; set; } = new JsonObject();
}

public sealed class ToolCall
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Arguments { get; init; } = "{}";
}

public sealed class AssistantReply
{
    public string Text { get; init; } = "";
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];
    public string StopReason { get; init; } = "end_turn";

    public bool HasToolCalls => ToolCalls.Count > 0;
}

public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ClientConfig _config;
    private readonly ConnectionEntry? _connection;
    private readonly ProviderKind _provider;
    private readonly string _model;

    public ApiClient(ClientConfig config, ConnectionEntry? connection = null)
    {
        _config = config;
        _connection = connection;
        _provider = connection?.Provider ?? ProviderKind.Anthropic;

        // A named connection carries its own address; otherwise the address comes
        // from the configuration, which is what a hand-typed gateway looks like.
        var baseUrl = string.IsNullOrWhiteSpace(connection?.Url) ? config.BaseUrl : connection.Url;

        // The model follows the connection when it names one, so switching provider
        // does not leave the previous provider's model behind.
        _model = !string.IsNullOrWhiteSpace(connection?.Model) ? connection.Model : config.Model;

        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(5),
        };

        // A named connection keeps its credential in secrets.json under its own
        // key, so two providers can be signed in at the same time. The gateway
        // key in client.json stays the fallback for hand-typed addresses.
        EffectiveKey = connection is null
            ? config.ApiKey
            : ClientConfig.GetSecret(connection.CredentialKey) ?? config.ApiKey;

        ApplyAuth(baseUrl);

        Wire.TraceId = null;
        Wire.Write("client.init", "base_url={0} · model={1} · max_tokens={2} · temperature={3} · provider={4} · api_key={5} · timeout=300s",
            baseUrl.TrimEnd('/') + "/",
            _model,
            config.MaxTokens,
            config.Temperature,
            _provider,
            string.IsNullOrWhiteSpace(EffectiveKey) ? "(none)" : Wire.Mask(EffectiveKey));
        Wire.Write("client.init", "trace file → {0}", Wire.LogDirectory);
    }

    /// <summary>The credential in use, from secrets.json or the configuration.</summary>
    private string? EffectiveKey { get; set; }

    /// <summary>
    /// Sends the credential the way the provider expects it. Each provider family
    /// reads the key from a different header, and a key sent in the wrong one is
    /// either ignored or rejected as a malformed request.
    /// </summary>
    private void ApplyAuth(string baseUrl)
    {
        var key = EffectiveKey;
        if (string.IsNullOrWhiteSpace(key)) return;

        switch (_connection?.Auth ?? AuthStyle.Bearer)
        {
            case AuthStyle.AnthropicKey:
                _http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", key);
                _http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                break;

            case AuthStyle.GoogleApiKey:
                _http.DefaultRequestHeaders.TryAddWithoutValidation("x-goog-api-key", key);
                break;

            case AuthStyle.Bearer:
                _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + key);
                break;

            case AuthStyle.None:
                break;
        }

        // Provider-specific headers from the preset (OpenRouter's referrer, etc.).
        if (_connection is not null)
        {
            foreach (var (name, value) in _connection.Headers)
                _http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }
    }

    public async Task<string> HealthAsync(CancellationToken ct = default)
    {
        var id = Wire.NewTraceId();
        Wire.TraceId = id;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Wire.Write("http.req", "GET health → {0}", _http.BaseAddress);

        using var response = await _http.GetAsync("health", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Wire.Timing("http.resp", clock, "{0} · {1} bytes", (int)response.StatusCode, body.Length);
        Wire.Json("http.body", body);

        response.EnsureSuccessStatusCode();
        return body;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var id = Wire.NewTraceId();
        Wire.TraceId = id;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Wire.Write("http.req", "GET v1/models → {0}", _http.BaseAddress);

        using var response = await _http.GetAsync("v1/models", ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        Wire.Timing("http.resp", clock, "{0} · {1} bytes", (int)response.StatusCode, json.Length);

        response.EnsureSuccessStatusCode();
        var list = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
                if (item.TryGetProperty("id", out var id2) && id2.GetString() is { } s)
                    list.Add(s);
        }
        Wire.Write("http.body", "models=[{0}]", string.Join(", ", list));
        return list;
    }

    /// <summary>
    /// One agent turn, client to gateway and back. <paramref name="traceId"/> is
    /// minted by the caller so the CLI's tool execution can be labelled with the
    /// same id the gateway and DeepSeek lines carry. <paramref name="sessionKey"/>
    /// is the conversation's identity: the gateway uses it to keep one remote
    /// DeepSeek chat open across the turns instead of creating one per request.
    /// </summary>
    public async Task<AssistantReply> SendAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSpec>? tools,
        string traceId,
        string sessionKey,
        CancellationToken ct = default)
    {
        Wire.TraceId = traceId;

        var payload = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = _config.MaxTokens,
            ["temperature"] = _config.Temperature,
        };

        if (_provider == ProviderKind.Anthropic)
        {
            payload["system"] = _config.System;
        }

        var messageArray = new JsonArray();
        foreach (var message in messages)
        {
            messageArray.Add(new JsonObject
            {
                ["role"] = message.Role,
                ["content"] = message.Content?.DeepClone(),
            });
        }
        payload["messages"] = messageArray;

        if (tools is { Count: > 0 })
        {
            var toolArray = new JsonArray();
            foreach (var tool in tools)
            {
                toolArray.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = tool.InputSchema.DeepClone(),
                });
            }
            payload["tools"] = toolArray;
        }

        var json = payload.ToJsonString();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var endpoint = _provider == ProviderKind.Anthropic ? "v1/messages" : "v1/chat/completions";
        Wire.Write("http.req", "POST {0} → {1} · history={2} msgs · tools={3} · {4} bytes",
            endpoint, _http.BaseAddress, messages.Count, tools?.Count ?? 0, json.Length);
        Wire.Json("http.req.body", json);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-trace-id", traceId);

        // Lets the gateway continue the DeepSeek chat this conversation already
        // has open instead of creating a new session for every request.
        if (!string.IsNullOrWhiteSpace(sessionKey))
            request.Headers.TryAddWithoutValidation("x-agent-session", sessionKey);

        using var response = await _http.SendAsync(request, ct);
        Wire.Write("http.resp.hdr", "HTTP {0} · server_trace={1}", (int)response.StatusCode,
            response.Headers.TryGetValues("x-trace-id", out var echoed) ? echoed.First() : "(not echoed)");

        var body = await response.Content.ReadAsStringAsync(ct);
        Wire.Timing("http.resp", clock, "{0} · {1} bytes", (int)response.StatusCode, body.Length);
        Wire.Json("http.resp.body", body);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {Truncate(body, 500)}");

        var reply = ParseReply(body);
        Wire.Write("http.parsed", "stop_reason={0} · text={1} chars · tool_calls=[{2}]",
            reply.StopReason, reply.Text.Length,
            string.Join(", ", reply.ToolCalls.Select(c => $"{c.Name} args={c.Arguments.Length} chars")));
        Wire.Block("http.parsed.text", reply.Text);
        foreach (var call in reply.ToolCalls)
            Wire.Json("http.parsed.tool", call.Arguments);

        return reply;
    }

    private static AssistantReply ParseReply(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() ?? "end_turn" : "end_turn";

        var text = new StringBuilder();
        var calls = new List<ToolCall>();

        // Anthropic: content array with type text/tool_use
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "text" && block.TryGetProperty("text", out var txt))
                {
                    text.Append(txt.GetString());
                }
                else if (type == "tool_use")
                {
                    var id = block.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                    var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var input = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                    calls.Add(new ToolCall { Id = id, Name = name, Arguments = input });
                }
            }
        }
        // OpenAI: choices[0].message.content + tool_calls
        else if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var message))
                {
                    if (message.TryGetProperty("content", out var msgContent) && msgContent.ValueKind == JsonValueKind.String)
                        text.Append(msgContent.GetString());

                    if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in toolCalls.EnumerateArray())
                        {
                            var id = tc.TryGetProperty("id", out var tci) ? tci.GetString() ?? "" : "";
                            // JsonElement is a struct, so the absence of the property has
                            // to be spelled as a nullable or the two arms of the
                            // conditional disagree on their type.
                            JsonElement? func = tc.TryGetProperty("function", out var f) ? f : null;
                            var name = func is not null && func.Value.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var args = func is not null && func.Value.TryGetProperty("arguments", out var a) ? a.GetRawText() : "{}";
                            calls.Add(new ToolCall { Id = id, Name = name, Arguments = args });
                        }
                    }
                }
            }
        }

        return new AssistantReply { Text = text.ToString(), ToolCalls = calls, StopReason = stopReason };
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    public void Dispose() => _http.Dispose();
}
