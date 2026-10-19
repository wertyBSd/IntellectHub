using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntellectHub;

internal sealed class ResponsesHandler : IRequestHandler
{
    private readonly ChatCompletionService _service;

    public ResponsesHandler(ChatCompletionService service) => _service = service;

    public bool CanHandle(string method, string path) =>
        method == "POST" && path == "/v1/responses";

    public async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        ResponsesRequest? request;
        try
        {
            var body = await RequestTrace.ReadAsync(context, ct);
            request = JsonSerializer.Deserialize<ResponsesRequest>(body);
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

        Wire.Write("body.in.sum", "model={0} · stream={1} · tools={2}",
            request.Model ?? "(none)", request.Stream, request.Tools?.Count ?? 0);

        CompletionResult result;
        try
        {
            var chatRequest = ToCompletionRequest(request);
            chatRequest.SessionKey = RequestContext.SessionKey(context);
            result = await _service.CompleteAsync(chatRequest, ct);
        }
        catch (UpstreamException ex)
        {
            Wire.Write("body.in.upstream", "{0} ({1}): {2}", ex.StatusCode, ex.ErrorKind ?? "upstream", ex.Message);
            await HttpResponses.WriteUpstreamErrorAsync(context, ex);
            return;
        }

        RequestTrace.Summarize("body.out.sum", result);

        var id = "resp_" + (result.MessageId ?? Guid.NewGuid().ToString("N"));
        var modelId = ModelRegistry.Canonicalize(request.Model);
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // The Responses API models a tool call as its own output item, not as message text.
        object[] output = result.HasToolCalls
            ? result.ToolCalls.Select(call => (object)new
            {
                type = "function_call",
                id = "fc_" + Guid.NewGuid().ToString("N"),
                call_id = "call_" + Guid.NewGuid().ToString("N")[..24],
                name = call.Name,
                arguments = call.Arguments,
                status = "completed",
            }).ToArray()
            :
            [
                new
                {
                    type = "message",
                    id = "msg_" + Guid.NewGuid().ToString("N"),
                    role = "assistant",
                    status = "completed",
                    content = new[]
                    {
                        new { type = "output_text", text = result.Content, annotations = Array.Empty<object>() },
                    },
                },
            ];

        var payload = JsonSerializer.Serialize(new
        {
            id,
            @object = "response",
            created_at = created,
            status = "completed",
            model = modelId,
            output,
            usage = new { input_tokens = 0, output_tokens = 0, total_tokens = 0 },
        });

        await HttpResponses.WriteJsonAsync(context, 200, payload);
    }

    private static ChatCompletionRequest ToCompletionRequest(ResponsesRequest request)
    {
        var messages = new List<ChatMessage>();

        if (request.Instructions.ValueKind == JsonValueKind.String)
        {
            var instructions = request.Instructions.GetString();
            if (!string.IsNullOrEmpty(instructions))
                messages.Add(new ChatMessage { Role = "system", Content = JsonSerializer.SerializeToElement(instructions) });
        }

        AppendInput(messages, request.Input);

        return new ChatCompletionRequest
        {
            Model = request.Model,
            Messages = messages,
            Stream = request.Stream,
            Tools = request.Tools,
        };
    }

    private static void AppendInput(List<ChatMessage> messages, JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
        {
            messages.Add(new ChatMessage { Role = "user", Content = input });
            return;
        }

        if (input.ValueKind != JsonValueKind.Array) return;

        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var role = item.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : "user";
            var content = item.TryGetProperty("content", out var c) ? c : item;

            messages.Add(new ChatMessage { Role = role, Content = content });
        }
    }

    private sealed class ResponsesRequest
    {
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("input")] public JsonElement Input { get; set; }
        [JsonPropertyName("instructions")] public JsonElement Instructions { get; set; }
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("tools")] public List<ToolDefinition>? Tools { get; set; }
    }
}
