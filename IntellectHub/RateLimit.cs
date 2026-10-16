using System.Text.RegularExpressions;

namespace IntellectHub;

/// <summary>
/// Detects DeepSeek throttling.
///
/// The important case is not the 429. DeepSeek's web backend usually throttles a
/// login with an HTTP 200 or 400 whose body carries a localized "too frequent"
/// message, or with an in-stream <c>event: hint</c> frame. A gateway that only
/// looks at the status code sees a normal reply, keeps hammering the login and
/// gets it banned. Everything here funnels into a single decision.
/// </summary>
internal static partial class RateLimit
{
    /// <summary>Localized wording DeepSeek uses to throttle, plus the English markers.</summary>
    [GeneratedRegex(
        @"(?<![a-z])rate[\s_-]*limit|too\s+many\s+requests|too\s+frequent|too\s+many\s+attempts|" +
        @"слишком\s+част|частые\s+(?:сообщения|запросы)|слишком\s+много\s+запросов|" +
        @"频繁|请求过多|请求次数过多|操作过于频繁",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RateLimitPattern();

    [GeneratedRegex(@"rate_?limit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FinishReasonPattern();

    [GeneratedRegex(@"(?:content|prompt|context).{0,40}(?:too\s+long|too\s+large|length|limit|maximum)|" +
                    @"maximum.{0,30}(?:context|token)|too\s+many\s+tokens|" +
                    @"содержани[ея]\s+слишком\s+длин|контекст.{0,30}(?:длин|лимит)|" +
                    @"内容.{0,12}(?:过长|太长)|上下文.{0,12}(?:过长|超出)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContextTooLongPattern();

    /// <summary>True when the text is a throttling signal.</summary>
    public static bool IsSignal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return RateLimitPattern().IsMatch(text);
    }

    /// <summary>True when the terminal reason marks the reply as throttled.</summary>
    public static bool IsFinishReason(string? finishReason)
    {
        if (string.IsNullOrWhiteSpace(finishReason)) return false;
        return FinishReasonPattern().IsMatch(finishReason);
    }

    /// <summary>
    /// True when the prompt itself is too large. Explicitly false for throttling:
    /// a sidelined login is not an oversized prompt, and compacting for one would
    /// silently shrink the conversation while the real problem is the cooldown.
    /// </summary>
    public static bool IsContextTooLong(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (IsSignal(text)) return false;
        return ContextTooLongPattern().IsMatch(text);
    }

    /// <summary>A short, safe fragment of the signal for the log.</summary>
    public static string Describe(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "rate limit";
        var flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= 160 ? flat : flat[..157] + "...";
    }
}