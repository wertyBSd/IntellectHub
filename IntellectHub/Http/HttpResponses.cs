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
        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }

    public static Task WriteErrorAsync(HttpListenerContext context, int status, string message, string type)
    {
        var json = JsonSerializer.Serialize(new { error = new { message, type } });
        return WriteJsonAsync(context, status, json);
    }

    public static void StartEventStream(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.SendChunked = true;
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    public static async Task WriteSseDataAsync(HttpListenerResponse response, string data, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes($"data: {data}\n\n");
        await response.OutputStream.WriteAsync(bytes, ct);
        await response.OutputStream.FlushAsync(ct);
    }
}
