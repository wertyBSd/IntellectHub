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

    public ApiClient(ClientConfig config)
    {
        _config = config;
        _http = new HttpClient
        {
            BaseAddress = new Uri(config.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(5),
        };
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + config.ApiKey);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", config.ApiKey);
        }
    }

    public async Task<string> HealthAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("health", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("v1/models", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var list = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
                if (item.TryGetProperty("id", out var id) && id.GetString() is { } s)
                    list.Add(s);
        }
        return list;
    }

    public async Task<AssistantReply> SendAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSpec>? tools,
        CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["model"] = _config.Model,
            ["max_tokens"] = _config.MaxTokens,
            ["temperature"] = _config.Temperature,
            ["system"] = _config.System,
        };

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

        using var response = await _http.PostAsync(
            "v1/messages",
            new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {Truncate(body, 500)}");

        return ParseReply(body);
    }

    private static AssistantReply ParseReply(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() ?? "end_turn" : "end_turn";

        var text = new StringBuilder();
        var calls = new List<ToolCall>();

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

        return new AssistantReply { Text = text.ToString(), ToolCalls = calls, StopReason = stopReason };
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    public void Dispose() => _http.Dispose();
}
