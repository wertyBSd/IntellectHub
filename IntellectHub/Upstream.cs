using System.Text;
using System.Text.Json;

namespace IntellectHub;

internal sealed class DeepSeekChallenge
{
    public string? Algorithm { get; set; }
    public string? Challenge { get; set; }
    public string? Salt { get; set; }
    public long ExpireAt { get; set; }
    public int Difficulty { get; set; }
    public string? Signature { get; set; }
}

internal interface IPowSolver
{
    Task<long> SolveAsync(DeepSeekChallenge challenge, string wasmUrl, CancellationToken ct);
}

internal static class DeepSeekHeaders
{
    public static Dictionary<string, string> Build(AccountConfig cfg) => new()
    {
        ["User-Agent"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36",
        ["x-client-platform"] = "web",
        ["x-client-version"] = "2.0.2",
        ["x-client-locale"] = "ru",
        ["x-client-timezone-offset"] = "14400",
        ["x-app-version"] = "2.0.2",
        ["Authorization"] = $"Bearer {cfg.Token ?? string.Empty}",
        ["x-hif-dliq"] = cfg.HifDliq ?? string.Empty,
        ["x-hif-leim"] = cfg.HifLeim ?? string.Empty,
        ["Origin"] = "https://chat.deepseek.com",
        ["Referer"] = "https://chat.deepseek.com/",
        ["Cookie"] = cfg.Cookie ?? string.Empty,
        ["Content-Type"] = "application/json",
    };
}

internal static class PowPayload
{
    public static string BuildBase64(DeepSeekChallenge challenge, long answer, string targetPath)
    {
        var json = JsonSerializer.Serialize(new
        {
            algorithm = challenge.Algorithm,
            challenge = challenge.Challenge,
            salt = challenge.Salt,
            answer,
            signature = challenge.Signature,
            target_path = targetPath,
        });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}

internal static class DeepSeekJson
{
    private static string? Text(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => el.GetRawText(),
        _ => null,
    };

    public static string? ExtractSessionId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
        if (!data.TryGetProperty("biz_data", out var biz)) return null;
        if (biz.TryGetProperty("chat_session", out var session) &&
            session.TryGetProperty("id", out var sid))
            return Text(sid);
        if (biz.TryGetProperty("id", out var id)) return Text(id);
        return null;
    }

    public static DeepSeekChallenge? ExtractChallenge(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
        if (!data.TryGetProperty("biz_data", out var biz)) return null;
        if (!biz.TryGetProperty("challenge", out var ch)) return null;
        return new DeepSeekChallenge
        {
            Algorithm = ch.TryGetProperty("algorithm", out var a) ? Text(a) : null,
            Challenge = ch.TryGetProperty("challenge", out var c) ? Text(c) : null,
            Salt = ch.TryGetProperty("salt", out var s) ? Text(s) : null,
            ExpireAt = ch.TryGetProperty("expire_at", out var e) && e.TryGetInt64(out var ev) ? ev : 0,
            Difficulty = ch.TryGetProperty("difficulty", out var d) && d.TryGetInt32(out var dv) ? dv : 0,
            Signature = ch.TryGetProperty("signature", out var sig) ? Text(sig) : null,
        };
    }

    public static string? ExtractResponseMessageId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("response_message_id", out var id)) return Text(id);
        return null;
    }
}

/// <summary>
/// Accumulates DeepSeek's completion stream into the full reply. The reply is never
/// emitted part-by-part: tool markup is only recognisable once the last token has
/// arrived, so everything is buffered until the stream ends.
/// </summary>
internal sealed class SseAccumulator
{
    private readonly List<Fragment> _fragments = new();
    private string _fullContent = "";
    private string _reasoning = "";
    private string _lastPath = "";
    private string _eventName = "";

    public string? MessageId { get; private set; }
    public string? FinishReason { get; private set; }
    public string Content => _fullContent;
    public string ReasoningContent => _reasoning;

    /// <summary>
    /// Set when the stream carried a throttling signal. DeepSeek delivers these as
    /// an <c>event: hint</c> frame on an otherwise-successful 200 stream, so the
    /// reply looks complete while nothing was actually produced.
    /// </summary>
    public bool RateLimited { get; private set; }

    public string? RateLimitDetail { get; private set; }

    public void Consume(string line)
    {
        // A blank line closes an SSE frame, so the event name does not leak into
        // the next one.
        if (string.IsNullOrEmpty(line))
        {
            _eventName = "";
            return;
        }

        // SSE frame headers select the meaning of the following data payload.
        if (line.StartsWith("event:", StringComparison.Ordinal))
        {
            _eventName = line[6..].Trim();
            return;
        }

        if (!line.StartsWith("data: ", StringComparison.Ordinal)) return;
        var json = line[6..];
        if (json.Length == 0) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("response_message_id", out var rid) && MessageId is null)
            {
                var id = ScalarText(rid);
                if (id is not null) MessageId = id;
            }

            if (root.TryGetProperty("finish_reason", out var fr))
            {
                var reason = ScalarText(fr);
                if (!string.IsNullOrEmpty(reason)) FinishReason = reason;
            }

            NoteThrottling(root);

            if (root.TryGetProperty("p", out var p) && p.ValueKind == JsonValueKind.String)
                _lastPath = p.GetString() ?? "";

            if (root.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Object &&
                v.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.Object)
            {
                if (resp.TryGetProperty("message_id", out var mid))
                {
                    var id = ScalarText(mid);
                    if (id is not null) MessageId = id;
                }
                if (resp.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    _fullContent = content.GetString() ?? _fullContent;
                if (resp.TryGetProperty("fragments", out var fragments) && fragments.ValueKind == JsonValueKind.Array)
                {
                    _fragments.Clear();
                    AppendFragments(fragments);
                }
                if (resp.TryGetProperty("finish_reason", out var rfr))
                {
                    var reason = ScalarText(rfr);
                    if (!string.IsNullOrEmpty(reason)) FinishReason = reason;
                }
            }

            if (root.TryGetProperty("v", out var value))
            {
                if (_lastPath == "response/fragments")
                {
                    AppendFragments(value);
                }
                else if (_lastPath == "response")
                {
                    ApplyPatchOperations(value);
                }
                else if (_lastPath == "response/fragments/-1/content" && value.ValueKind != JsonValueKind.Object && value.ValueKind != JsonValueKind.Array)
                {
                    if (_fragments.Count > 0)
                    {
                        var last = _fragments[^1];
                        last.Content = (last.Content ?? "") + ScalarText(value);
                        Rebuild();
                    }
                }
                else if (_lastPath == "response/content" && value.ValueKind != JsonValueKind.Object && value.ValueKind != JsonValueKind.Array)
                {
                    _fullContent += ScalarText(value);
                }
                else if (_lastPath == "response/finish_reason")
                {
                    var reason = ScalarText(value);
                    if (!string.IsNullOrEmpty(reason)) FinishReason = reason;
                }
                else if (_lastPath == "response/status")
                {
                    var status = ScalarText(value);
                    if (status is not null && status != "FINISHED") FinishReason = status;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // A malformed frame is skipped; the rest of the stream still counts.
        }
    }

    /// <summary>
    /// Records a throttling signal carried by the current frame, either as an
    /// explicit finish reason or as localized text in the frame body.
    /// </summary>
    private void NoteThrottling(JsonElement root)
    {
        if (RateLimited) return;
        if (_eventName != "hint" && !RateLimit.IsFinishReason(FinishReason)) return;

        var detail = root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : null;

        if (RateLimit.IsFinishReason(FinishReason) || RateLimit.IsSignal(detail))
        {
            RateLimited = true;
            RateLimitDetail = string.IsNullOrWhiteSpace(detail) ? FinishReason : detail;
        }
    }

    private void AppendFragments(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) AddFragment(item);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            AddFragment(value);
        }
        Rebuild();
    }

    private void AddFragment(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return;
        var type = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var content = item.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        _fragments.Add(new Fragment { Type = type, Content = content });
    }

    private void ApplyPatchOperations(JsonElement ops)
    {
        if (ops.ValueKind != JsonValueKind.Array) return;
        foreach (var op in ops.EnumerateArray())
        {
            if (op.ValueKind != JsonValueKind.Object) continue;
            if (op.TryGetProperty("p", out var p) && p.ValueKind == JsonValueKind.String && p.GetString() == "fragments" &&
                op.TryGetProperty("o", out var o) && o.ValueKind == JsonValueKind.String && o.GetString() == "APPEND" &&
                op.TryGetProperty("v", out var v))
            {
                AppendFragments(v);
            }
        }
    }

    private void Rebuild()
    {
        var response = new StringBuilder();
        var reasoning = new StringBuilder();
        foreach (var fragment in _fragments)
        {
            if (fragment.Content is null) continue;
            if (fragment.Type is "RESPONSE" or "SEARCH") response.Append(fragment.Content);
            else if (fragment.Type is "THINK" or "REASONING") reasoning.Append(fragment.Content);
        }

        if (response.Length > 0) _fullContent = response.ToString();
        _reasoning = reasoning.ToString();
    }

    private static string? ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
        _ => null,
    };

    private sealed class Fragment
    {
        public string? Type { get; init; }
        public string? Content { get; set; }
    }
}