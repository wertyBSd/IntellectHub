using System.Net;
using System.Text.Json;

namespace IntellectHub;

internal sealed class ModelsHandler : IRequestHandler
{
    public bool CanHandle(string method, string path) =>
        method == "GET" && path == "/v1/models";

    public Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var models = ModelRegistry.SupportedIds.Select(id => new
        {
            id,
            @object = "model",
            created = 0,
            owned_by = "intellecthub",
            capabilities = ModelRegistry.Configs[id].Capabilities
        });

        var json = JsonSerializer.Serialize(new { @object = "list", data = models });
        return HttpResponses.WriteJsonAsync(context, 200, json);
    }
}
