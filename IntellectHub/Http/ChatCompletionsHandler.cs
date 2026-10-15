using System.Net;
using System.Text;
using System.Text.Json;

namespace IntellectHub;

internal sealed class ChatCompletionsHandler : IRequestHandler
{
    private readonly ChatCompletionService _service;

    public ChatCompletionsHandler(ChatCompletionService service) => _service = service;

    public bool CanHandle(string method, string path) =>
        method == "POST" && path == "/v1/chat/completions";

    public async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        ChatCompletionRequest? request;
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            request = JsonSerializer.Deserialize<ChatCompletionRequest>(body);
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

        if (request.Stream)
        {
            await StreamAsync(context, request, ct);
            return;
        }

        try
        {
            var result = await _service.CompleteAsync(request, ct);
            var id = "chatcmpl-" + (result.MessageId ?? Guid.NewGuid().ToString("N"));
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var modelId = ModelRegistry.Canonicalize(request.Model);

            var payload = JsonSerializer.Serialize(new
            {
                id,
                @object = "chat.completion",
                created,
                model = modelId,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = result.Content },
                        finish_reason = NormalizeFinishReason(result.FinishReason),
                    },
                },
                usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 },
            });

            await HttpResponses.WriteJsonAsync(context, 200, payload);
        }
        catch (UpstreamException ex)
        {
            await HttpResponses.WriteErrorAsync(context, ex.StatusCode, ex.Message, ErrorType(ex.StatusCode));
        }
    }

    private async Task StreamAsync(HttpListenerContext context, ChatCompletionRequest request, CancellationToken ct)
    {
        var response = context.Response;
        var id = "chatcmpl-" + Guid.NewGuid().ToString("N");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var modelId = ModelRegistry.Canonicalize(request.Model);

        var started = false;
        var finishReason = "stop";

        async Task EmitAsync(SseDelta delta)
        {
            if (!started)
            {
                HttpResponses.StartEventStream(response);
                await WriteChunkAsync(response, id, created, modelId, new { role = "assistant" }, null, ct);
                started = true;
            }

            if (!string.IsNullOrEmpty(delta.Content))
                await WriteChunkAsync(response, id, created, modelId, new { content = delta.Content }, null, ct);

            if (delta.FinishReason is { } fr)
                finishReason = NormalizeFinishReason(fr);
        }

        try
        {
            await _service.StreamAsync(request, EmitAsync, ct);
        }
        catch (UpstreamException ex)
        {
            if (!started)
            {
                await HttpResponses.WriteErrorAsync(context, ex.StatusCode, ex.Message, ErrorType(ex.StatusCode));
                return;
            }

            var error = JsonSerializer.Serialize(new { error = new { message = ex.Message, type = ErrorType(ex.StatusCode) } });
            await HttpResponses.WriteSseDataAsync(response, error, ct);
            await HttpResponses.WriteSseDataAsync(response, "[DONE]", ct);
            response.Close();
            return;
        }

        if (!started)
        {
            HttpResponses.StartEventStream(response);
            await WriteChunkAsync(response, id, created, modelId, new { role = "assistant" }, null, ct);
        }

        await WriteChunkAsync(response, id, created, modelId, new { }, finishReason, ct);
        await HttpResponses.WriteSseDataAsync(response, "[DONE]", ct);
        response.Close();
    }

    private static Task WriteChunkAsync(HttpListenerResponse response, string id, long created, string modelId,
        object delta, string? finishReason, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model = modelId,
            choices = new[] { new { index = 0, delta, finish_reason = finishReason } },
        });
        return HttpResponses.WriteSseDataAsync(response, payload, ct);
    }

    private static string ErrorType(int status) => status switch
    {
        401 or 403 => "authentication_error",
        429 => "rate_limit_error",
        503 => "service_unavailable",
        _ => "upstream_error",
    };

    private static string NormalizeFinishReason(string? reason) => reason switch
    {
        "stop" or null => "stop",
        "length" => "length",
        _ => reason,
    };
}
