using System.Net;

namespace IntellectHub;

internal interface IRequestHandler
{
    bool CanHandle(string method, string path);

    bool RequiresAuth => true;

    Task HandleAsync(HttpListenerContext context, CancellationToken ct);
}
