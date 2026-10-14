using System.Net;
using System.Text;

namespace IntellectHub;

internal sealed class ProxyServer
{
    private readonly AppConfig _config;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _listener?.IsListening == true;

    public ProxyServer(AppConfig config) => _config = config;

    public void Start()
    {
        if (IsRunning) return;

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{_config.Host}:{_config.Port}/");
        listener.Start();

        _listener = listener;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
    }

    public void Stop()
    {
        var listener = _listener;
        if (listener is null) return;

        _listener = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        try { listener.Stop(); } catch { }
        try { listener.Close(); } catch { }
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    AppLog.Error($"listener: {ex.Message}");
                return;
            }

            _ = Task.Run(() => HandleAsync(context), token);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url?.AbsolutePath ?? "/";

        try
        {
            switch (path)
            {
                case "/health":
                    await WriteJsonAsync(context, 200, "{\"status\":\"ok\"}");
                    break;
                case "/v1/models":
                    await WriteJsonAsync(context, 200, "{\"object\":\"list\",\"data\":[]}");
                    break;
                default:
                    AppLog.Warn($"{request.HttpMethod} {path} -> 501 (не портировано)");
                    await WriteJsonAsync(context, 501, "{\"error\":{\"message\":\"Not implemented yet\",\"type\":\"not_implemented\"}}");
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"{request.HttpMethod} {path} -> {ex.Message}");
            try { context.Response.Abort(); } catch { }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int status, string json)
    {
        var buffer = Encoding.UTF8.GetBytes(json);
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }
}
