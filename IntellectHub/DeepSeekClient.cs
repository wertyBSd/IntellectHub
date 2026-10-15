using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace IntellectHub;

internal sealed class UpstreamException : Exception
{
    public int StatusCode { get; }
    public long? RetryAfterMs { get; }

    public UpstreamException(string message, int statusCode = 502, long? retryAfterMs = null) : base(message)
    {
        StatusCode = statusCode;
        RetryAfterMs = retryAfterMs;
    }
}

internal interface IPowSolverProvider
{
    IPowSolver? Solver { get; }
}

internal sealed record UpstreamResult(string Content, string ReasoningContent, string? MessageId, string? FinishReason);

internal sealed class DeepSeekClient
{
    private const string BaseUrl = "https://chat.deepseek.com";
    private const string PowPath = "/api/v0/chat/create_pow_challenge";
    private const string SessionCreatePath = "/api/v0/chat_session/create";
    private const string CompletionPath = "/api/v0/chat/completion";

    private readonly HttpClient _http;
    private readonly IPowSolverProvider _pow;

    public DeepSeekClient(HttpClient http, IPowSolverProvider pow)
    {
        _http = http;
        _pow = pow;
    }

    public async Task<UpstreamResult> CompleteAsync(
        Account account,
        ModelConfig model,
        string prompt,
        string? chatSessionId,
        string? parentMessageId,
        bool? thinkingEnabled,
        bool? searchEnabled,
        CancellationToken ct)
    {
        using var request = await BuildCompletionRequestAsync(account, model, prompt, chatSessionId, parentMessageId, thinkingEnabled, searchEnabled, ct);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureCompletionStreamAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await ReadStreamAsync(stream, ct);
    }

    public async Task StreamAsync(
        Account account,
        ModelConfig model,
        string prompt,
        Func<SseDelta, Task> onDelta,
        CancellationToken ct)
    {
        using var request = await BuildCompletionRequestAsync(account, model, prompt, null, null, null, null, ct);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureCompletionStreamAsync(response, ct);

        var accumulator = new SseAccumulator();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (accumulator.Consume(line) && accumulator.LastDelta is { } delta)
                await onDelta(delta);
        }
    }

    public async Task<string> CreateSessionAsync(Account account, CancellationToken ct)
    {
        using var request = BuildRequest(HttpMethod.Post, SessionCreatePath, account, null);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new UpstreamException($"session create HTTP {(int)response.StatusCode}: {Truncate(text)}", (int)response.StatusCode);
        var id = DeepSeekJson.ExtractSessionId(text);
        if (string.IsNullOrEmpty(id))
            throw new UpstreamException($"session create returned no id: {Truncate(text)}");
        return id;
    }

    private async Task<HttpRequestMessage> BuildCompletionRequestAsync(
        Account account,
        ModelConfig model,
        string prompt,
        string? chatSessionId,
        string? parentMessageId,
        bool? thinkingEnabled,
        bool? searchEnabled,
        CancellationToken ct)
    {
        var powHeader = await CreatePowHeaderAsync(account, CompletionPath, ct);
        var sessionId = chatSessionId ?? await CreateSessionAsync(account, ct);

        var body = JsonSerializer.Serialize(new
        {
            chat_session_id = sessionId,
            parent_message_id = parentMessageId,
            model_type = model.ModelType,
            prompt,
            ref_file_ids = Array.Empty<string>(),
            thinking_enabled = thinkingEnabled ?? model.ThinkingEnabled,
            search_enabled = searchEnabled ?? model.SearchEnabled,
            action = (string?)null,
            preempt = false,
        });

        var request = BuildRequest(HttpMethod.Post, CompletionPath, account, powHeader);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<string> CreatePowHeaderAsync(Account account, string targetPath, CancellationToken ct)
    {
        var solver = _pow.Solver ?? throw new UpstreamException(
            "PoW solver not configured: the DeepSeek SHA3 WASM module could not be loaded.", 503);

        var body = JsonSerializer.Serialize(new { target_path = targetPath });
        using var request = BuildRequest(HttpMethod.Post, PowPath, account, null);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new UpstreamException($"pow challenge HTTP {(int)response.StatusCode}: {Truncate(text)}", (int)response.StatusCode);

        var challenge = DeepSeekJson.ExtractChallenge(text)
            ?? throw new UpstreamException($"pow response has no challenge: {Truncate(text)}");

        var wasmUrl = account.Config.WasmUrl ?? string.Empty;
        var answer = await solver.SolveAsync(challenge, wasmUrl, ct);
        return PowPayload.BuildBase64(challenge, answer, targetPath);
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string path, Account account, string? powHeader)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        foreach (var (key, value) in DeepSeekHeaders.Build(account.Config))
            request.Headers.TryAddWithoutValidation(key, value);
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        if (powHeader is not null)
            request.Headers.TryAddWithoutValidation("X-DS-PoW-Response", powHeader);
        return request;
    }

    private static async Task EnsureCompletionStreamAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new UpstreamException($"completion HTTP {(int)response.StatusCode}: {Truncate(text)}", (int)response.StatusCode, RetryAfterMs(response));
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new UpstreamException($"DeepSeek returned JSON instead of a stream: {Truncate(text)}");
        }
    }

    private static async Task<UpstreamResult> ReadStreamAsync(Stream stream, CancellationToken ct)
    {
        var accumulator = new SseAccumulator();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            accumulator.Consume(line);
        }
        return new UpstreamResult(accumulator.Content, accumulator.ReasoningContent, accumulator.MessageId, accumulator.FinishReason);
    }

    private static long? RetryAfterMs(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null) return null;
        if (retryAfter.Delta is { } delta) return (long)Math.Max(0, delta.TotalMilliseconds);
        if (retryAfter.Date is { } date) return (long)Math.Max(0, (date - DateTimeOffset.UtcNow).TotalMilliseconds);
        return null;
    }

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200];
}
