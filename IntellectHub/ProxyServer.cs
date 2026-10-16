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

        try
        {
            await _router.HandleAsync(context, token);
        }
        catch (Exception ex)
        {
            AppLog.Error($"{method} {path} -> {ex.Message}");
            try { context.Response.Abort(); } catch { }
        }
    }
}
