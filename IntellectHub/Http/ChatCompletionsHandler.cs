using System.Net;
using System.Text;
using System.Text.Json;

namespace IntellectHub;

internal static class RequestContext
{
    /// <summary>
    /// Identity of the conversation behind a request. Clients that send
    /// x-agent-session keep one remote DeepSeek chat for the whole conversation
    /// instead of paying for a new session on every turn.
    /// </summary>
    public static string? SessionKey(HttpListenerContext context)
    {
        var header = context.Request.Headers["x-agent-session"];
        if (header is not { Length: > 0 }) return null;

        var value = header.Trim();
        if (value.Length > 200) value = value[..200];

        return string.IsNullOrEmpty(value) ? null : value;
    }
}

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

        request.SessionKey = RequestContext.SessionKey(context);

        // The upstream reply is always buffered: tool markup is only recognisable once
        // the reply is complete, so streaming it out would emit raw DSML mid-token.
        CompletionResult result;
        try
        {
            result = await _service.CompleteAsync(request, ct);
        }
        catch (UpstreamException ex)
        {
            await HttpResponses.WriteErrorAsync(context, ex.StatusCode, ex.Message, ErrorType(ex));
            return;
        }

        var ids = ToolCallIds(result);

        if (request.Stream)
        {
            await StreamAsync(context, request, result, ids, ct);
            return;
        }

        var payload = BuildCompletionPayload(
            "chatcmpl-" + (result.MessageId ?? Guid.NewGuid().ToString("N")),
            ModelRegistry.Canonicalize(request.Model),
            result,
            ids);

        await HttpResponses.WriteJsonAsync(context, 200, payload);
    }

    /// <summary>
    /// The chat.completion body. A tool call carries <c>content: null</c> and
    /// <c>finish_reason: "tool_calls"</c>, so the markup never rides along as text.
    /// </summary>
    internal static string BuildCompletionPayload(string id, string modelId, CompletionResult result,
        IReadOnlyList<string> ids) =>
        JsonSerializer.Serialize(new
        {
            id,
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = modelId,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = result.HasToolCalls ? null : result.Content,
                        tool_calls = result.HasToolCalls
                            ? result.ToolCalls.Select((call, index) => new
                            {
                                id = ids[index],
                                type = "function",
                                @function = new { name = call.Name, arguments = call.Arguments },
                            }).ToArray()
                            : null,
                    },
                    finish_reason = result.HasToolCalls
                        ? "tool_calls"
                        : NormalizeFinishReason(result.FinishReason),
                },
            },
            usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 },
        });

    private async Task StreamAsync(HttpListenerContext context, ChatCompletionRequest request,
        CompletionResult result, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var response = context.Response;
        var id = "chatcmpl-" + (result.MessageId ?? Guid.NewGuid().ToString("N"));
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var modelId = ModelRegistry.Canonicalize(request.Model);

        HttpResponses.StartEventStream(response);
        await WriteChunkAsync(response, id, created, modelId, new { role = "assistant" }, null, ct);

        if (result.HasToolCalls)
        {
            // OpenAI streams the identity of each call first, then its arguments.
            await WriteChunkAsync(response, id, created, modelId, new
            {
                tool_calls = result.ToolCalls.Select((call, index) => new
                {
                    index,
                    id = ids[index],
                    type = "function",
                    @function = new { name = call.Name, arguments = "" },
                }).ToArray(),
            }, null, ct);

            await WriteChunkAsync(response, id, created, modelId, new
            {
                tool_calls = result.ToolCalls.Select((call, index) => new
                {
                    index,
                    @function = new { arguments = call.Arguments },
                }).ToArray(),
            }, null, ct);
        }
        else if (result.Content.Length > 0)
        {
            await WriteChunkAsync(response, id, created, modelId, new { content = result.Content }, null, ct);
        }

        await WriteChunkAsync(response, id, created, modelId, new { },
            result.HasToolCalls ? "tool_calls" : NormalizeFinishReason(result.FinishReason), ct);
        await HttpResponses.WriteSseDataAsync(response, "[DONE]", ct);
        response.Close();
    }

    private static IReadOnlyList<string> ToolCallIds(CompletionResult result) =>
        result.ToolCalls.Select(_ => "call_" + Guid.NewGuid().ToString("N")[..24]).ToList();

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

    internal static string ErrorType(UpstreamException ex)
    {
        // A specific cause always wins over the generic status-derived name.
        if (ex.ErrorKind is { Length: > 0 } kind) return kind;
        if (ex is MalformedToolCallException) return MalformedToolCallException.ErrorTypeName;

        return ex.StatusCode switch
        {
            401 or 403 => "authentication_error",
            429 => "rate_limit_error",
            503 => "service_unavailable",
            504 => "request_timeout",
            _ => "upstream_error",
        };
    }

    private static string NormalizeFinishReason(string? reason) => reason switch
    {
        null or "" or "stop" => "stop",
        "length" => "length",
        _ => reason,
    };
}