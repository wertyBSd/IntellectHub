using System.Net;
using System.Text.Json;

namespace IntellectHub;

internal sealed class ReadyzHandler : IRequestHandler
{
    private readonly AccountPool _pool;

    public ReadyzHandler(AccountPool pool) => _pool = pool;

    public bool CanHandle(string method, string path) =>
        method == "GET" && path == "/readyz";

    public bool RequiresAuth => false;

    public Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        _pool.Reload();
        var (ready, total) = _pool.Snapshot();
        var json = JsonSerializer.Serialize(new
        {
            ready = ready > 0,
            ready_accounts = ready,
            total_accounts = total
        });
        return HttpResponses.WriteJsonAsync(context, ready > 0 ? 200 : 503, json);
    }
}
