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
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            request = JsonSerializer.Deserialize<AnthropicRequest>(body);
        }
        catch (JsonException ex)
        {
            await HttpResponses.WriteErrorAsync(context, 400, $"Invalid JSON body: {ex.Message}", "invalid_request_error");
            return;
        }

        if (request is null)
        {
            await HttpResponses.WriteErrorAsync(context, 400, "Empty request body", "invalid_request_error");
            return;
        }

        try
        {
            var result = await _service.CompleteAsync(ToCompletionRequest(request), ct);
            var id = "msg_" + (result.MessageId ?? Guid.NewGuid().ToString("N"));
            var modelId = ModelRegistry.Canonicalize(request.Model);

            var payload = JsonSerializer.Serialize(new
            {
                id,
                type = "message",
                role = "assistant",
                model = modelId,
                content = new[] { new { type = "text", text = result.Content } },
                stop_reason = NormalizeStopReason(result.FinishReason),
                stop_sequence = (string?)null,
                usage = new { input_tokens = 0, output_tokens = 0 },
            });

            await HttpResponses.WriteJsonAsync(context, 200, payload);
        }
        catch (UpstreamException ex)
        {
            var type = ex.StatusCode switch
            {
                401 or 403 => "authentication_error",
                429 => "rate_limit_error",
                503 => "service_unavailable",
                _ => "upstream_error",
            };
            await HttpResponses.WriteErrorAsync(context, ex.StatusCode, ex.Message, type);
        }
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
        };
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
    }

    private sealed class AnthropicMessage
    {
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("content")] public JsonElement Content { get; set; }
    }
}
