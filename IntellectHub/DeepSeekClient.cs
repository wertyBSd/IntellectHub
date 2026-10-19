using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace IntellectHub;

internal class UpstreamException : Exception
{
    public int StatusCode { get; }
    public long? RetryAfterMs { get; }

    /// <summary>Overrides the status-derived error type when the cause is specific.</summary>
    public string? ErrorKind { get; }

    public UpstreamException(string message, int statusCode = 502, long? retryAfterMs = null,
        string? errorKind = null) : base(message)
    {
        StatusCode = statusCode;
        RetryAfterMs = retryAfterMs;
        ErrorKind = errorKind;
    }
}

/// <summary>Known values for <see cref="UpstreamException.ErrorKind"/>.</summary>
internal static class ErrorKinds
{
    public const string RateLimit = "rate_limit";
    public const string Overloaded = "overloaded";
    public const string RequestTimeout = "request_timeout";
    public const string UpstreamTimeout = "upstream_timeout";
    public const string ContextTooLong = "context_length_exceeded";
    public const string ConcurrentChatBlocked = "concurrent_chat_blocked";
    public const string MalformedToolCall = "malformed_tool_call";
}

/// <summary>The login was throttled. The account is sidelined and the caller may rotate.</summary>
internal sealed class UpstreamRateLimitException(string message, long? retryAfterMs = null)
    : UpstreamException(message, 429, retryAfterMs, ErrorKinds.RateLimit);

internal interface IPowSolverProvider
{
    IPowSolver? Solver { get; }
}

/// <summary>
/// The one upstream call the completion service makes. Kept narrow so the
/// failover, queue and deadline logic can be exercised without a live DeepSeek.
/// </summary>
internal interface IDeepSeekCompletion
{
    /// <summary>Starts a turn. A null session id creates one; a session id continues it.</summary>
    Task<UpstreamResult> CompleteAsync(Account account, ModelConfig model, string prompt,
        string? chatSessionId, string? parentMessageId, CancellationToken ct);
}

internal sealed record UpstreamResult(string Content, string ReasoningContent, string? MessageId, string? FinishReason)
{
    /// <summary>The remote chat this reply belongs to, so the next turn can continue it.</summary>
    public string? ChatSessionId { get; init; }
}

/// <summary>A finished stream plus whether it was actually a throttling notice.</summary>
internal sealed record StreamRead(UpstreamResult Completion, bool RateLimited, string? RateLimitDetail);

/// <summary>
/// DeepSeek replied with tool-call markup that does not parse into an in-scope call.
/// Surfaced as a distinct error type so the client sees a clear failure instead of
/// raw DSML delivered as assistant text.
/// </summary>
internal sealed class MalformedToolCallException(string message) : UpstreamException(message, 502)
{
    public const string ErrorTypeName = "malformed_tool_call";
}

internal sealed class DeepSeekClient : IDeepSeekCompletion
{
    private const string BaseUrl = "https://chat.deepseek.com";
    private const string PowPath = "/api/v0/chat/create_pow_challenge";
    private const string SessionCreatePath = "/api/v0/chat_session/create";
    private const string CompletionPath = "/api/v0/chat/completion";

    private readonly HttpClient _http;
    private readonly IPowSolverProvider _pow;
    private readonly int _upstreamTimeoutSeconds;

    public DeepSeekClient(HttpClient http, IPowSolverProvider pow, int upstreamTimeoutSeconds = 60)
    {
        _http = http;
        _pow = pow;
        _upstreamTimeoutSeconds = upstreamTimeoutSeconds > 0 ? upstreamTimeoutSeconds : 60;
    }

    public Task<UpstreamResult> CompleteAsync(Account account, ModelConfig model, string prompt,
        CancellationToken ct) => CompleteAsync(account, model, prompt, null, null, ct);

    public Task<UpstreamResult> CompleteAsync(
        Account account,
        ModelConfig model,
        string prompt,
        string? chatSessionId,
        string? parentMessageId,
        CancellationToken ct) =>
        CompleteAsync(account, model, prompt, chatSessionId, parentMessageId, null, null, ct);

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
        // One attempt gets its own budget so a wedged stream cannot hold the account
        // for the whole request deadline.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(TimeSpan.FromSeconds(_upstreamTimeoutSeconds));
        var token = attempt.Token;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Wire.Write("ds.begin", "account={0} · model={1} · prompt={2} chars · chat_session={3} · parent={4} · thinking={5} · search={6} · budget={7}s",
            account.Id, model.ModelType, prompt.Length,
            chatSessionId ?? "(new)", parentMessageId ?? "-",
            thinkingEnabled ?? model.ThinkingEnabled, searchEnabled ?? model.SearchEnabled,
            _upstreamTimeoutSeconds);

        // Resolved up front: a session created here has to be handed back so the
        // next turn can continue it instead of paying for another one.
        var sessionId = chatSessionId ?? await CreateSessionAsync(account, token);

        using var request = await BuildCompletionRequestAsync(account, model, prompt, sessionId, parentMessageId, thinkingEnabled, searchEnabled, token);
        Wire.Block("ds.prompt", prompt);

        var sendClock = System.Diagnostics.Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        Wire.Timing("ds.resp", sendClock, "HTTP {0} · {1} · {2} · retry-after={3}",
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.MediaType ?? "-",
            DescribeContentLength(response),
            RetryAfterMs(response)?.ToString() ?? "-");

        await EnsureCompletionStreamAsync(response, token);

        await using var stream = await response.Content.ReadAsStreamAsync(token);

        StreamRead result;
        try
        {
            result = await ReadStreamAsync(stream, token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Wire.Write("ds.timeout", "no answer within {0}s (headers took {1} ms)", _upstreamTimeoutSeconds, sendClock.ElapsedMilliseconds);
            throw new UpstreamException(
                $"DeepSeek did not answer within {_upstreamTimeoutSeconds}s.", 504, errorKind: ErrorKinds.UpstreamTimeout);
        }

        // A 200 stream can still be a throttling notice rather than an answer.
        if (result.RateLimited)
        {
            Wire.Write("ds.throttled", "stream carried a rate-limit notice: {0}", result.RateLimitDetail ?? "(no detail)");
            throw new UpstreamRateLimitException(
                $"completion stream throttled: {RateLimit.Describe(result.RateLimitDetail)}");
        }

        var completed = result.Completion with { ChatSessionId = sessionId };
        Wire.Timing("ds.done", clock,
            "content={0} chars · reasoning={1} chars · message_id={2} · finish={3} · session={4}",
            completed.Content.Length, completed.ReasoningContent.Length,
            completed.MessageId ?? "-", completed.FinishReason ?? "-", sessionId);
        Wire.Block("ds.reply", completed.Content);
        Wire.Block("ds.reasoning", completed.ReasoningContent);

        return completed;
    }

    private static string DescribeContentLength(HttpResponseMessage response)
    {
        var length = response.Content.Headers.ContentLength;
        return length is { } value ? $"{value} bytes" : "chunked";
    }

    public async Task<string> CreateSessionAsync(Account account, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Wire.Write("ds.session.req", "account={0} · POST {1}", account.Id, SessionCreatePath);

        using var request = BuildRequest(HttpMethod.Post, SessionCreatePath, account, null);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Wire.Timing("ds.session.resp", clock, "HTTP {0} · {1} bytes", (int)response.StatusCode, text.Length);
        Wire.Json("ds.session.body", text);

        if (!response.IsSuccessStatusCode || string.IsNullOrEmpty(DeepSeekJson.ExtractSessionId(text)))
        {
            // Session creation is where a throttled login usually announces itself first.
            if (RateLimit.IsSignal(text))
            {
                Wire.Write("ds.session.err", "throttled: {0}", RateLimit.Describe(text));
                throw new UpstreamRateLimitException(
                    $"session create throttled: {RateLimit.Describe(text)}", RetryAfterMs(response));
            }

            if (!response.IsSuccessStatusCode)
            {
                Wire.Write("ds.session.err", "HTTP {0}", (int)response.StatusCode);
                throw new UpstreamException($"session create HTTP {(int)response.StatusCode}: {Truncate(text)}", (int)response.StatusCode);
            }
        }

        var id = DeepSeekJson.ExtractSessionId(text);
        if (string.IsNullOrEmpty(id))
        {
            Wire.Write("ds.session.err", "HTTP {0} but no session id in the body", (int)response.StatusCode);
            throw new UpstreamException($"session create returned no id: {Truncate(text)}");
        }

        Wire.Write("ds.session.ok", "chat_session_id={0}", id);
        return id;
    }

    private async Task<HttpRequestMessage> BuildCompletionRequestAsync(
        Account account,
        ModelConfig model,
        string prompt,
        string sessionId,
        string? parentMessageId,
        bool? thinkingEnabled,
        bool? searchEnabled,
        CancellationToken ct)
    {
        var powHeader = await CreatePowHeaderAsync(account, CompletionPath, ct);

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
        Wire.Json("ds.req.body", body);
        return request;
    }

    private async Task<string> CreatePowHeaderAsync(Account account, string targetPath, CancellationToken ct)
    {
        var solver = _pow.Solver ?? throw new UpstreamException(
            "PoW solver not configured: the DeepSeek SHA3 WASM module could not be loaded.", 503);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var body = JsonSerializer.Serialize(new { target_path = targetPath });
        using var request = BuildRequest(HttpMethod.Post, PowPath, account, null);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Wire.Write("ds.pow.resp", "HTTP {0} · {1} ms · {2} bytes", (int)response.StatusCode, clock.ElapsedMilliseconds, text.Length);
        Wire.Json("ds.pow.challenge", text);

        if (!response.IsSuccessStatusCode)
        {
            if (RateLimit.IsSignal(text))
            {
                Wire.Write("ds.pow.err", "throttled: {0}", RateLimit.Describe(text));
                throw new UpstreamRateLimitException(
                    $"pow challenge throttled: {RateLimit.Describe(text)}", RetryAfterMs(response));
            }

            Wire.Write("ds.pow.err", "HTTP {0}", (int)response.StatusCode);
            throw new UpstreamException($"pow challenge HTTP {(int)response.StatusCode}: {Truncate(text)}", (int)response.StatusCode);
        }

        var challenge = DeepSeekJson.ExtractChallenge(text)
            ?? throw new UpstreamException($"pow response has no challenge: {Truncate(text)}");

        var wasmUrl = account.Config.WasmUrl ?? string.Empty;
        var solveClock = System.Diagnostics.Stopwatch.StartNew();
        var answer = await solver.SolveAsync(challenge, wasmUrl, ct);
        Wire.Write("ds.pow.solved", "algorithm={0} · difficulty={1} · answer={2} · solved in {3} ms",
            challenge.Algorithm ?? "?", challenge.Difficulty, answer, solveClock.ElapsedMilliseconds);
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

        // Credentials are masked by name, so what is logged is the header *shape*.
        Wire.Write("ds.req", "{0} {1}", method, BaseUrl + path);
        Wire.Headers("ds.req.hdr", request.Headers);
        return request;
    }

    /// <summary>
    /// Turns a non-stream completion response into the right error. DeepSeek rarely
    /// uses 429 for throttling; it answers 200 or 400 with a JSON body carrying a
    /// localized notice, so the status code alone is not enough to spot it.
    /// </summary>
    private static async Task EnsureCompletionStreamAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        var isJson = contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase);

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            var retryAfter = RetryAfterMs(response);
            Wire.Write("ds.resp.err", "HTTP {0} · retry-after={1}", (int)response.StatusCode, retryAfter?.ToString() ?? "-");
            Wire.Json("ds.resp.body", text);

            if (RateLimit.IsSignal(text))
                throw new UpstreamRateLimitException(
                    $"completion HTTP {(int)response.StatusCode} throttled: {RateLimit.Describe(text)}", retryAfter);

            if (RateLimit.IsContextTooLong(text))
                throw new UpstreamException(
                    $"context too long: {Truncate(text)}", 400, errorKind: ErrorKinds.ContextTooLong);

            throw new UpstreamException(
                $"completion HTTP {(int)response.StatusCode}: {Truncate(text)}",
                (int)response.StatusCode, retryAfter);
        }

        if (!isJson) return;

        // A 200 that is JSON instead of a stream: either a business error or a throttle.
        var body = await response.Content.ReadAsStringAsync(ct);
        Wire.Write("ds.resp.json", "HTTP 200 but application/json, not text/event-stream · {0} bytes", body.Length);
        Wire.Json("ds.resp.body", body);
        if (RateLimit.IsSignal(body))
            throw new UpstreamRateLimitException($"completion throttled: {RateLimit.Describe(body)}");

        if (RateLimit.IsContextTooLong(body))
            throw new UpstreamException(
                $"context too long: {Truncate(body)}", 400, errorKind: ErrorKinds.ContextTooLong);

        throw new UpstreamException($"DeepSeek returned JSON instead of a stream: {Truncate(body)}");
    }

    private static async Task<StreamRead> ReadStreamAsync(Stream stream, CancellationToken ct)
    {
        var accumulator = new SseAccumulator();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // A full reply can run to thousands of frames. The first ones carry the
        // protocol shape and any throttle notice, so those go in verbatim; the rest
        // are counted, because a token-by-token dump buries everything else.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lines = 0;
        var logged = 0;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            lines++;

            if (logged < 40 && !string.IsNullOrWhiteSpace(line))
            {
                logged++;
                Wire.Block("ds.sse", line);
            }

            accumulator.Consume(line);
        }

        var messageId = ResolveMessageId(accumulator.MessageId);
        Wire.Timing("ds.sse.end", clock, "{0} SSE lines · first {1} captured verbatim · message_id={2}",
            lines, logged, messageId ?? "(rejected: not a UUID)");

        return new StreamRead(
            new UpstreamResult(accumulator.Content, accumulator.ReasoningContent, messageId, accumulator.FinishReason),
            accumulator.RateLimited,
            accumulator.RateLimitDetail);
    }

    /// <summary>
    /// Keeps a message id only if it is shaped like one.
    ///
    /// The stream carries both "response_message_id" and "response.message_id", and
    /// the first one to arrive wins when nothing overrides it. In the field that
    /// value has been the small per-response sequence number — 2, 3, 4 — not an id.
    /// Handing that back as the next request's parent_message_id makes DeepSeek
    /// reject the whole request with a 422 and a deserialisation error naming the
    /// column, which reads like a protocol bug and is really a bad id.
    ///
    /// A rejected id is dropped rather than repaired: without one the next turn
    /// simply opens a new session, which is slower but correct.
    /// </summary>
    private static string? ResolveMessageId(string? candidate) =>
        candidate is not null && Guid.TryParse(candidate, out _) ? candidate : null;

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
