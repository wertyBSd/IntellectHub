using System.Net;

namespace IntellectHub;

internal sealed class HealthHandler : IRequestHandler
{
    public bool CanHandle(string method, string path) =>
        method == "GET" && path == "/health";

    public bool RequiresAuth => false;

    public Task HandleAsync(HttpListenerContext context, CancellationToken ct) =>
        HttpResponses.WriteJsonAsync(context, 200, "{\"status\":\"ok\"}");
}
