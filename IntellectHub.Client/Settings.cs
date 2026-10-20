using System.Globalization;

namespace IntellectHub.Client;

/// <summary>
/// One configurable setting: how it is named, how it is shown, how it is edited
/// and what counts as a valid value.
///
/// The point of the type is that every setting validates the same way and reports
/// the same kind of error, so a bad value is rejected before it is saved rather
/// than surfacing later as a failed request.
/// </summary>
internal sealed record Setting(
    string Name,
    string[] Aliases,
    string Hint,
    Func<ClientConfig, string> Read,
    Action<ClientConfig, string> Write,
    Func<string, string?> Validate,
    Func<ClientConfig, string>? Default = null)
{
    /// <summary>Changing this one needs a new connection to the server.</summary>
    public bool NeedsReconnect { get; init; }

    /// <summary>Rendered with the value blanked out.</summary>
    public bool Secret { get; init; }

    /// <summary>Whether this setting has a sensible "clear it" state.</summary>
    public bool Clearable { get; init; }

    public string Display(ClientConfig config) => Secret
        ? Mask(Read(config))
        : (Read(config) ?? string.Empty);

    /// <summary>
    /// The value <c>/config reset</c> restores. Held here rather than in a switch
    /// beside the screen, so a setting cannot be reset to another one's default.
    /// </summary>
    public string DefaultValue => Default?.Invoke(new ClientConfig()) ?? "";

    public bool Matches(string token) =>
        string.Equals(Name, token, StringComparison.OrdinalIgnoreCase) ||
        Aliases.Any(alias => string.Equals(alias, token, StringComparison.OrdinalIgnoreCase));

    private static string Mask(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(none)";
        return "****" + value[^Math.Min(4, value.Length)..];
    }

    public static IReadOnlyList<Setting> All { get; } =
    [
        new Setting(
            "server", ["url", "host", "port"],
            "where the gateway listens",
            c => c.BaseUrl,
            (c, v) => c.BaseUrl = NormalizeBaseUrl(v),
            ValidateBaseUrl,
            c => c.BaseUrl)
        { NeedsReconnect = true },

        new Setting(
            "model", ["models"],
            "which model answers",
            c => c.Model,
            (c, v) => c.Model = v,
            v => string.IsNullOrWhiteSpace(v) ? "model name cannot be empty" : null,
            c => c.Model),

        new Setting(
            "key", ["apikey", "token"],
            "gateway API key; leave empty if the server has none",
            c => c.ApiKey ?? "",
            (c, v) => c.ApiKey = string.IsNullOrWhiteSpace(v) ? null : v,
            _ => null,
            _ => "")
        { Secret = true, Clearable = true, NeedsReconnect = true },

        new Setting(
            "system", ["prompt"],
            "standing instructions sent with every message",
            c => c.System,
            (c, v) => c.System = v,
            _ => null,
            c => c.System)
        { Clearable = true },

        new Setting(
            "maxtokens", ["tokens", "max"],
            "reply length ceiling",
            c => c.MaxTokens.ToString(CultureInfo.InvariantCulture),
            (c, v) => c.MaxTokens = int.Parse(v, CultureInfo.InvariantCulture),
            ValidateMaxTokens,
            c => c.MaxTokens.ToString(CultureInfo.InvariantCulture)),

        new Setting(
            "temperature", ["temp"],
            "0 is literal, 1 is the model default, 2 is loose",
            c => c.Temperature.ToString("0.##", CultureInfo.InvariantCulture),
            (c, v) => c.Temperature = double.Parse(v, CultureInfo.InvariantCulture),
            ValidateTemperature,
            c => c.Temperature.ToString("0.##", CultureInfo.InvariantCulture)),

        new Setting(
            "approvals", ["approval", "auto", "yes"],
            ToolRisk.Describe(Approvals.All),
            c => c.ApprovalMode,
            (c, v) => c.ApprovalMode = ToolRisk.Normalize(v),
            ValidateApproval,
            c => c.ApprovalMode),

        new Setting(
            "selfcheck", ["self-check", "check"],
            "verify subtask execution by checking file changes",
            c => c.SelfCheck ? "on" : "off",
            (c, v) => c.SelfCheck = v.Trim().ToLowerInvariant() is "on" or "true" or "yes" or "1",
            v => v.Trim().ToLowerInvariant() is "on" or "off" or "true" or "false" or "yes" or "no" or "1" or "0"
                ? null
                : "use on or off",
            c => c.SelfCheck ? "on" : "off"),
    ];

    /// <summary>Accepts "127.0.0.1:9657" as readily as the full URL.</summary>
    public static string NormalizeBaseUrl(string value) =>
        value.Contains("://") ? value.TrimEnd('/') : "http://" + value.TrimEnd('/');

    public static Setting? Find(string token) => All.FirstOrDefault(s => s.Matches(token));

    /// <summary>Suggests the setting the user meant when the name is a near miss.</summary>
    public static string? Suggest(string token)
    {
        var limit = Math.Max(1, token.Length / 2);
        Setting? best = null;
        var bestDistance = int.MaxValue;

        foreach (var setting in All)
        {
            foreach (var name in new[] { setting.Name }.Concat(setting.Aliases))
            {
                var distance = Distance(token, name, limit);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = setting;
            }
        }

        return bestDistance <= limit ? best!.Name : null;
    }

    /// <summary>
    /// Levenshtein distance, bailing out as soon as no row can still fit inside
    /// <paramref name="limit"/>. The early exit has to report a value known to
    /// exceed the limit rather than the partial row minimum: returning the partial
    /// value can hand back a candidate the caller then wrongly accepts.
    /// </summary>
    private static int Distance(string a, string b, int limit)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowBest = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowBest = Math.Min(rowBest, current[j]);
            }
            (previous, current) = (current, previous);
            if (rowBest > limit) return limit + 1;
        }

        return previous[b.Length];
    }

    private static string? ValidateBaseUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "server URL cannot be empty";

        var text = value.Contains("://") ? value : "http://" + value;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return $"'{value}' is not a valid URL";

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return $"'{uri.Scheme}' is not supported, use http or https";

        if (string.IsNullOrEmpty(uri.Host)) return $"'{value}' has no host";

        return null;
    }

    private static string? ValidateMaxTokens(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return $"'{value}' is not a whole number";

        return parsed > 0 ? null : "maxTokens must be at least 1";
    }

    private static string? ValidateTemperature(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return $"'{value}' is not a number";

        return parsed is >= 0 and <= 2 ? null : "temperature must be between 0 and 2";
    }

    private static string? ValidateApproval(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Equals("y", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return null;

        return Enum.TryParse<Approvals>(trimmed, ignoreCase: true, out _)
            ? null
            : $"use ask, writes or all (you typed '{value}')";
    }
}