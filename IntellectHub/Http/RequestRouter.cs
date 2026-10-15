using System.Net;

namespace IntellectHub;

internal sealed class RequestRouter
{
    private readonly IReadOnlyList<IRequestHandler> _handlers;

    public RequestRouter(IReadOnlyList<IRequestHandler> handlers) => _handlers = handlers;

    public static RequestRouter CreateDefault(ChatCompletionService chatService, AccountPool pool) => new(new IRequestHandler[]
    {
        new HealthHandler(),
        new ReadyzHandler(pool),
        new ModelsHandler(),
        new ChatCompletionsHandler(chatService),
        new AnthropicMessagesHandler(chatService),
        new ResponsesHandler(chatService),
    });

    public IRequestHandler Resolve(string method, string path)
    {
        foreach (var handler in _handlers)
        {
            if (handler.CanHandle(method, path)) return handler;
        }
        return NotFoundHandler.Instance;
    }

    public Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var method = context.Request.HttpMethod;
        var path = context.Request.Url?.AbsolutePath ?? "/";
        return Resolve(method, path).HandleAsync(context, ct);
    }
}
