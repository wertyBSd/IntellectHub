using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntellectHub;

internal sealed class AnthropicMessagesHandler : IRequestHandler
{
    private readonly ChatCompletionService _service;

    public AnthropicMessagesHandler(ChatCompletionService service) => _service = service;

    public bool CanHandle(string method, string path) =>
        method == "POST" && path == "/v1/messages";

    public async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        AnthropicRequest? request;
        string rawBody;
        try
        {
            rawBody = await RequestTrace.ReadAsync(context, ct);
            request = JsonSerializer.Deserialize<AnthropicRequest>(rawBody);
        }
        catch (JsonException ex)
        {
            Wire.Write("body.in.err", "invalid JSON: {0}", ex.Message);
            await HttpResponses.WriteErrorAsync(context, 400, $"Invalid JSON body: {ex.Message}", "invalid_request_error");
            return;
        }

        if (request is null)
        {
            await HttpResponses.WriteErrorAsync(context, 400, "Empty request body", "invalid_request_error");
            return;
        }

        Wire.Write("body.in.sum", "model={0} · stream={1} · max_tokens={2} · messages={3} · tools={4}",
            request.Model ?? "(none)", request.Stream, request.MaxTokens,
            request.Messages?.Count ?? 0, request.Tools?.Count ?? 0);

        CompletionResult result;
        try
        {
            var chatRequest = ToCompletionRequest(request);
            chatRequest.SessionKey = RequestContext.SessionKey(context);
            Wire.Write("body.in.norm", "session_key={0} · messages={1} · tools={2}",
                chatRequest.SessionKey ?? "(new session)", chatRequest.Messages.Count, chatRequest.Tools?.Count ?? 0);
            result = await _service.CompleteAsync(chatRequest, ct);
        }
        catch (UpstreamException ex)
        {
            Wire.Write("body.in.upstream", "{0} ({1}): {2}", ex.StatusCode, ex.ErrorKind ?? "upstream", ex.Message);
            await HttpResponses.WriteUpstreamErrorAsync(context, ex);
            return;
        }

        RequestTrace.Summarize("body.out.sum", result);

        // Anthropic carries tool calls as tool_use blocks inside the content array.
        var content = new List<object>();
        if (result.HasToolCalls)
        {
            foreach (var call in result.ToolCalls)
                content.Add(new { type = "tool_use", id = "toolu_" + Guid.NewGuid().ToString("N")[..24], name = call.Name, input = ParseArguments(call.Arguments) });
        }
        else if (result.Content.Length > 0)
        {
            content.Add(new { type = "text", text = result.Content });
        }

        var payload = JsonSerializer.Serialize(new
        {
            id = "msg_" + (result.MessageId ?? Guid.NewGuid().ToString("N")),
            type = "message",
            role = "assistant",
            model = ModelRegistry.Canonicalize(request.Model),
            content,
            stop_reason = result.HasToolCalls ? "tool_use" : NormalizeStopReason(result.FinishReason),
            stop_sequence = (string?)null,
            usage = new { input_tokens = 0, output_tokens = 0 },
        });

        await HttpResponses.WriteJsonAsync(context, 200, payload);
    }

    private static ChatCompletionRequest ToCompletionRequest(AnthropicRequest request)
    {
        var messages = new List<ChatMessage>();

        if (request.System.ValueKind is JsonValueKind.String or JsonValueKind.Array)
        {
            var system = PromptFormatter.NormalizeContent(request.System);
            if (!string.IsNullOrEmpty(system))
                messages.Add(new ChatMessage { Role = "system", Content = JsonSerializer.SerializeToElement(system) });
        }

        if (request.Messages is not null)
        {
            foreach (var message in request.Messages)
                messages.Add(new ChatMessage { Role = message.Role, Content = message.Content });
        }

        return new ChatCompletionRequest
        {
            Model = request.Model,
            Messages = messages,
            Stream = request.Stream,
            Tools = ToToolDefinitions(request.Tools),
        };
    }

    /// <summary>Maps Anthropic's flat tool shape onto the OpenAI function envelope.</summary>
    private static List<ToolDefinition>? ToToolDefinitions(List<AnthropicTool>? tools)
    {
        if (tools is not { Count: > 0 }) return null;

        return tools
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
            .Select(tool => new ToolDefinition
            {
                Type = "function",
                Function = new ToolDefinitionFunction
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    Parameters = tool.InputSchema.ValueKind == JsonValueKind.Object
                        ? tool.InputSchema
                        : null,
                },
            })
            .ToList();
    }

    private static JsonElement ParseArguments(string arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    private static string NormalizeStopReason(string? reason) => reason switch
    {
        "length" => "max_tokens",
        _ => "end_turn",
    };

    private sealed class AnthropicRequest
    {
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("system")] public JsonElement System { get; set; }
        [JsonPropertyName("messages")] public List<AnthropicMessage>? Messages { get; set; }
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("tools")] public List<AnthropicTool>? Tools { get; set; }
    }

    private sealed class AnthropicMessage
    {
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("content")] public JsonElement Content { get; set; }
    }

    private sealed class AnthropicTool
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("input_schema")] public JsonElement InputSchema { get; set; }
    }
}