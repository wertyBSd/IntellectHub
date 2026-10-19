using System.Net;
using System.Text;
using System.Text.Json;

namespace IntellectHub;

internal static class HttpResponses
{
    public static async Task WriteJsonAsync(HttpListenerContext context, int status, string json)
    {
        var buffer = Encoding.UTF8.GetBytes(json);
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = buffer.Length;

        // Single choke point for every JSON the gateway emits — handlers, auth
        // failures and upstream errors alike — so the reply leg of every exchange
        // is captured without each handler having to remember to trace.
        Wire.Write("body.out", "{0} {1} · {2} bytes", status, Describe(response), buffer.Length);
        Wire.Json("body.out.json", json);

        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }

    private static string Describe(HttpListenerResponse response)
    {
        try { return $"trace={Wire.TraceId ?? response.Headers["x-trace-id"] ?? "?"}"; }
        catch { return "trace=?"; }
    }

    public static Task WriteErrorAsync(HttpListenerContext context, int status, string message, string type,
        long? retryAfterMs = null)
    {
        // Integrators key their backoff off the header, not off the message text.
        if (retryAfterMs is { } ms && ms > 0)
        {
            try
            {
                context.Response.Headers["Retry-After"] =
                    Math.Max(1, (int)Math.Ceiling(ms / 1000d)).ToString();
            }
            catch
            {
                // Headers can be closed already; the status still carries the meaning.
            }
        }

        var json = JsonSerializer.Serialize(new { error = new { message, type } });
        return WriteJsonAsync(context, status, json);
    }

    public static Task WriteUpstreamErrorAsync(HttpListenerContext context, UpstreamException ex) =>
        WriteErrorAsync(context, ex.StatusCode, ex.Message,
            ChatCompletionsHandler.ErrorType(ex), ex.RetryAfterMs);

    public static void StartEventStream(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.SendChunked = true;
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        Wire.Write("sse.out", "stream opened (chunked)");
    }

    public static async Task WriteSseDataAsync(HttpListenerResponse response, string data, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes($"data: {data}\n\n");
        Wire.Json("sse.out.frame", data);
        await response.OutputStream.WriteAsync(bytes, ct);
        await response.OutputStream.FlushAsync(ct);
    }
}
