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

internal sealed record UpstreamChunk(string? Content, string? MessageId, string? FinishReason);

internal sealed record SseDelta(string? Content, string? Reasoning, string? FinishReason, string? MessageId);

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
    public static string? ExtractSessionId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
        if (!data.TryGetProperty("biz_data", out var biz)) return null;
        if (biz.TryGetProperty("chat_session", out var session) &&
            session.TryGetProperty("id", out var sid))
            return sid.GetString();
        if (biz.TryGetProperty("id", out var id)) return id.GetString();
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
            Algorithm = ch.TryGetProperty("algorithm", out var a) ? a.GetString() : null,
            Challenge = ch.TryGetProperty("challenge", out var c) ? c.GetString() : null,
            Salt = ch.TryGetProperty("salt", out var s) ? s.GetString() : null,
            ExpireAt = ch.TryGetProperty("expire_at", out var e) && e.TryGetInt64(out var ev) ? ev : 0,
            Difficulty = ch.TryGetProperty("difficulty", out var d) && d.TryGetInt32(out var dv) ? dv : 0,
            Signature = ch.TryGetProperty("signature", out var sig) ? sig.GetString() : null,
        };
    }

    public static string? ExtractResponseMessageId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("response_message_id", out var id)) return id.GetString();
        return null;
    }
}

internal sealed class SseParser
{
    private string _lastPath = "";

    public SseDelta? Consume(string dataLine)
    {
        if (string.IsNullOrEmpty(dataLine) || !dataLine.StartsWith("data: ", StringComparison.Ordinal)) return null;
        var json = dataLine[6..];
        if (json.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? messageId = null;
            string? finishReason = null;
            string? content = null;
            string? reasoning = null;

            if (root.TryGetProperty("response_message_id", out var rid))
                messageId = rid.GetString();
            if (root.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                finishReason = fr.GetString();
            if (root.TryGetProperty("p", out var p) && p.ValueKind == JsonValueKind.String)
                _lastPath = p.GetString() ?? "";
            if (root.TryGetProperty("v", out var v) && v.ValueKind != JsonValueKind.Object)
            {
                if (_lastPath == "response/content")
                    content = v.GetString();
                else if (_lastPath == "response/thinking")
                    reasoning = v.GetString();
                else if (_lastPath == "response/finish_reason" && v.ValueKind == JsonValueKind.String)
                    finishReason = v.GetString();
                else if (_lastPath == "response/status" && v.ValueKind == JsonValueKind.String && v.GetString() != "FINISHED")
                    finishReason = v.GetString();
            }

            if (content is null && reasoning is null && finishReason is null && messageId is null)
                return null;

            return new SseDelta(content, reasoning, finishReason, messageId);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed class SseAccumulator
{
    private readonly SseParser _parser = new();
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();

    public string? MessageId { get; private set; }
    public string? FinishReason { get; private set; }
    public string Content => _content.ToString();
    public string ReasoningContent => _reasoning.ToString();

    public void Consume(string dataLine)
    {
        var delta = _parser.Consume(dataLine);
        if (delta is null) return;

        if (delta.MessageId is not null) MessageId ??= delta.MessageId;
        if (delta.FinishReason is not null) FinishReason = delta.FinishReason;
        if (delta.Content is not null) _content.Append(delta.Content);
        if (delta.Reasoning is not null) _reasoning.Append(delta.Reasoning);
    }
}
