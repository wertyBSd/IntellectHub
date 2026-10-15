using System.Net;

namespace IntellectHub;

internal interface IRequestHandler
{
    bool CanHandle(string method, string path);

    Task HandleAsync(HttpListenerContext context, CancellationToken ct);
}
