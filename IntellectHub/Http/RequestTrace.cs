using System.Net;
using System.Text;

namespace IntellectHub;

/// <summary>
/// Body-level tracing for the HTTP handlers. Every request the gateway accepts goes
/// through <see cref="ReadAsync"/> and every reply through
/// <see cref="HttpResponses.WriteJsonAsync"/>, so this is the one place that has to
/// know about the wire between the client and the completion pipeline.
/// </summary>
internal static class RequestTrace
{
    /// <summary>
    /// Establishes the correlation id for one inbound request. The client's own id
    /// wins so its trace file and this one label the same turn identically; without
    /// one, a server id is minted. Everything nested under the call inherits it.
    /// </summary>
    public static string BeginTrace(HttpListenerContext context)
    {
        var id = context.Request.Headers["x-trace-id"];
        if (string.IsNullOrWhiteSpace(id)) id = Wire.NewTraceId();
        Wire.TraceId = id;

        // Echoed so the client can log the server's view of the same turn.
        try { context.Response.Headers["x-trace-id"] = id; } catch { /* headers may be closed */ }
        return id;
    }

    /// <summary>
    /// Drains and traces an inbound body. The handler keeps working from the returned
    /// string; <see cref="HttpListenerRequest.InputStream"/> is read-only, so the
    /// buffer cannot be pushed back for a second reader.
    /// </summary>
    public static async Task<string> ReadAsync(HttpListenerContext context, CancellationToken ct, string tag = "body.in")
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);

        Wire.Write(tag, "{0} bytes read", body.Length);
        Wire.Json(tag + ".json", body);
        return body;
    }

    /// <summary>Summarises what the completion pipeline produced, before it is serialised.</summary>
    public static void Summarize(string tag, CompletionResult result)
    {
        Wire.Write(tag, "stop_reason={0} · content={1} chars · tools=[{2}] · message_id={3}",
            result.FinishReason,
            result.Content.Length,
            string.Join(", ", result.ToolCalls.Select(call => $"{call.Name}({call.Arguments.Length} chars)")),
            result.MessageId ?? "-");
        Wire.Block(tag + ".text", result.Content);
    }
}