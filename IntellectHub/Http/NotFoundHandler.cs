using System.Net;

namespace IntellectHub;

internal sealed class NotFoundHandler : IRequestHandler
{
    public static readonly NotFoundHandler Instance = new();

    private NotFoundHandler() { }

    public bool CanHandle(string method, string path) => true;

    public Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var method = context.Request.HttpMethod;
        var path = context.Request.Url?.AbsolutePath ?? "/";
        AppLog.Warn($"{method} {path} -> 501 (не портировано)");
        return HttpResponses.WriteErrorAsync(context, 501, "Not implemented yet", "not_implemented");
    }
}
