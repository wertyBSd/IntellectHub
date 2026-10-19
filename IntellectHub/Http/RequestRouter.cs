using System.Net;

namespace IntellectHub;

internal sealed class RequestRouter
{
    private readonly IReadOnlyList<IRequestHandler> _handlers;
    private readonly ApiKeyStore _keys;

    public RequestRouter(IReadOnlyList<IRequestHandler> handlers, ApiKeyStore keys)
    {
        _handlers = handlers;
        _keys = keys;
    }

    public static RequestRouter CreateDefault(ChatCompletionService chatService, AccountPool pool, ApiKeyStore keys) =>
        new(new IRequestHandler[]
        {
            new HealthHandler(),
            new ReadyzHandler(pool),
            new ModelsHandler(),
            new ChatCompletionsHandler(chatService),
            new AnthropicMessagesHandler(chatService),
            new ResponsesHandler(chatService),
        }, keys);

    public IRequestHandler Resolve(string method, string path)
    {
        foreach (var handler in _handlers)
        {
            if (handler.CanHandle(method, path)) return handler;
        }
        return NotFoundHandler.Instance;
    }

    public async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var method = context.Request.HttpMethod;
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var handler = Resolve(method, path);
        Wire.Write("route", "→ {0} {1} · auth={2}", method, path, handler.RequiresAuth ? "required" : "open");

        if (handler.RequiresAuth && _keys.HasKeys)
        {
            var raw = ApiKeyStore.ExtractKey(context.Request);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var status = _keys.Validate(raw, now, out var record);
            Wire.Write("auth", "presented {0} · verdict={1}{2}",
                Wire.Mask(raw), status,
                record is null ? "" : $" · key={record.Prefix}");
            if (status != KeyStatus.Ok)
            {
                var (code, message) = Describe(status);
                Wire.Write("auth.deny", $"{code} {message}");
                await HttpResponses.WriteErrorAsync(context, code, message, "authentication_error");
                return;
            }

            _keys.RecordUse(record!, now);
        }
        else if (handler.RequiresAuth)
        {
            // No keys provisioned at all: the gateway is open, which is the default
            // install state and worth seeing explicitly in a trace.
            Wire.Write("auth", "no keys configured · request allowed without a credential");
        }

        await handler.HandleAsync(context, ct);
    }

    private static (int Code, string Message) Describe(KeyStatus status) => status switch
    {
        KeyStatus.Disabled => (403, "API key is disabled"),
        KeyStatus.Expired => (403, "API key has expired"),
        KeyStatus.DailyLimit => (429, "Daily request limit reached for this API key"),
        KeyStatus.MonthlyLimit => (429, "Monthly request limit reached for this API key"),
        _ => (401, "Missing or invalid API key"),
    };
}
