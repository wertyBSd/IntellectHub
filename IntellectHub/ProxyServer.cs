using System.Diagnostics;
using System.Net;

namespace IntellectHub;

internal sealed class ProxyServer
{
    private readonly AppConfig _config;
    private readonly AppServices _services;
    private readonly RequestRouter _router;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _listener?.IsListening == true;
    public ApiKeyStore ApiKeys => _services.ApiKeys;
    public AccountPool Pool => _services.Pool;

    public ProxyServer(AppConfig config)
    {
        _config = config.Normalize();
        _services = new AppServices(_config);
        _router = RequestRouter.CreateDefault(
            new ChatCompletionService(_services.Pool, _services.Client, _config),
            _services.Pool,
            _services.ApiKeys);
    }

    public void Start()
    {
        if (IsRunning) return;

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{_config.Host}:{_config.Port}/");
        listener.Start();

        _listener = listener;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));

        // Shown in the console so a tester knows where the trace is before they start.
#if DEBUG
        Wire.Write("server.start", $"gateway listening on http://{_config.Host}:{_config.Port}/ · trace → {Wire.LogDirectory}");
        AppLog.Info($"DEBUG: трассировка запросов → {Wire.FilePath}");
#endif
    }

    public void Stop()
    {
        var listener = Interlocked.Exchange(ref _listener, null);
        if (listener is null) return;

        var cts = Interlocked.Exchange(ref _cts, null);
        try { cts?.Cancel(); } catch { }

        try { listener.Stop(); } catch { }
        try { listener.Close(); } catch { }

        cts?.Dispose();
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

            _ = Task.Run(() => HandleAsync(context, token), token);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken token)
    {
        var method = context.Request.HttpMethod;
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var query = context.Request.Url?.Query ?? "";

        // Prefer the client's id so both trace files label the same turn the same
        // way; fall back to one of ours. Everything downstream inherits it through
        // the async flow, which is what makes the DeepSeek lines join up.
        RequestTrace.BeginTrace(context);

        var clock = Stopwatch.StartNew();
        Wire.Write("http.in", "{0} {1}{2} · client={3} · ua={4}",
            method, path, query,
            context.Request.RemoteEndPoint?.ToString() ?? "?",
            Wire.Mask(context.Request.UserAgent));
        Wire.Headers("http.in.hdr", context.Request.Headers);

        try
        {
            await _router.HandleAsync(context, token);
            Wire.Timing("http.out", clock, DescribeResponse(context.Response));
        }
        catch (Exception ex)
        {
            Wire.Timing("http.err", clock, $"{method} {path} → {ex.GetType().Name}: {ex.Message}");
            Wire.Block("http.err.st", ex.ToString());
            AppLog.Error($"{method} {path} -> {ex.Message}");
            try { context.Response.Abort(); } catch { }
        }
    }

    /// <summary>Status, media type and size of what actually reached the caller.</summary>
    private static string DescribeResponse(HttpListenerResponse response)
    {
        try
        {
            var length = response.ContentLength64;
            var size = length >= 0 ? $"{length} bytes" : "streamed";
            return $"{response.StatusCode} {response.ContentType ?? "-"} · {size}";
        }
        catch
        {
            return $"{response.StatusCode} (response already closed)";
        }
    }
}
