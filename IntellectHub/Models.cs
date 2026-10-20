namespace IntellectHub;

internal sealed class ModelCapabilities
{
    public bool Reasoning { get; init; }
    public bool WebSearch { get; init; }
    public bool Files { get; init; }
    public bool Vision { get; init; }
}

internal sealed class ModelConfig
{
    public required string ModelType { get; init; }
    public bool ThinkingEnabled { get; init; }
    public bool SearchEnabled { get; init; }
    public required string RealModel { get; init; }
    public required ModelCapabilities Capabilities { get; init; }
    public bool Supported { get; init; } = true;
}

internal static class ModelRegistry
{
    public const string DefaultModelId = "deepseek-v4-flash";

    private static readonly ModelCapabilities Full = new()
    {
        Reasoning = true, WebSearch = true, Files = true, Vision = true
    };

    private static readonly ModelCapabilities NoThink = new()
    {
        Reasoning = false, WebSearch = true, Files = true, Vision = true
    };

    private static readonly ModelCapabilities NoThinkNoSearch = new()
    {
        Reasoning = false, WebSearch = false, Files = true, Vision = true
    };

    private static readonly ModelCapabilities ThinkNoSearch = new()
    {
        Reasoning = true, WebSearch = false, Files = true, Vision = true
    };

    public static readonly IReadOnlyDictionary<string, ModelConfig> Configs =
        new Dictionary<string, ModelConfig>(StringComparer.Ordinal)
        {
            // Default models: no web search. DeepSeek's native web_search is a
            // built-in tool this gateway cannot run, so enabling it on the default
            // model makes the model call web_search and the turn ends in a 502.
            ["deepseek-v4-flash"] = new()
            {
                ModelType = "default", ThinkingEnabled = false, SearchEnabled = false,
                RealModel = "DeepSeek-V4.1-Flash", Capabilities = NoThinkNoSearch,
            },
            ["deepseek-v4-flash-thinking"] = new()
            {
                ModelType = "default", ThinkingEnabled = true, SearchEnabled = false,
                RealModel = "DeepSeek-V4.1-Flash (thinking)", Capabilities = ThinkNoSearch,
            },
            // Web search only on explicit -search aliases, exactly as the reference
            // gateway (FreeDeepseekAPI) does it.
            ["deepseek-v4-flash-search"] = new()
            {
                ModelType = "default", ThinkingEnabled = false, SearchEnabled = true,
                RealModel = "DeepSeek-V4.1-Flash (web search)", Capabilities = NoThink,
            },
            ["deepseek-v4-flash-thinking-search"] = new()
            {
                ModelType = "default", ThinkingEnabled = true, SearchEnabled = true,
                RealModel = "DeepSeek-V4.1-Flash (thinking + web search)", Capabilities = Full,
            },
            ["deepseek-v4-flash-nosearch"] = new()
            {
                ModelType = "default", ThinkingEnabled = false, SearchEnabled = false,
                RealModel = "DeepSeek-V4.1-Flash", Capabilities = NoThinkNoSearch,
            },
            ["deepseek-v4-flash-thinking-nosearch"] = new()
            {
                ModelType = "default", ThinkingEnabled = true, SearchEnabled = false,
                RealModel = "DeepSeek-V4.1-Flash (thinking)", Capabilities = ThinkNoSearch,
            },
        };

    public static string Canonicalize(string? model)
    {
        var trimmed = model?.Trim();
        var id = (string.IsNullOrEmpty(trimmed) ? DefaultModelId : trimmed).ToLowerInvariant();

        id = System.Text.RegularExpressions.Regex.Replace(id, @"\[(?:1m|max|high|low)\]$", "");

        if (id == "deepseek-flash" || id.StartsWith("deepseek-flash-", StringComparison.Ordinal))
            id = "deepseek-v4-flash" + id["deepseek-flash".Length..];

        // Try the exact id first — this keeps -search variants as separate models
        // instead of collapsing them onto the non-search base.
        if (Configs.ContainsKey(id)) return id;

        if (id.EndsWith("-search", StringComparison.Ordinal))
        {
            var baseId = id[..^"-search".Length];
            if (Configs.ContainsKey(baseId)) return baseId;
        }

        if (id.Contains("opus", StringComparison.Ordinal) || id.Contains("opus-4", StringComparison.Ordinal))
            return "deepseek-v4-flash-thinking";
        if (System.Text.RegularExpressions.Regex.IsMatch(id, @"claude[-_. ]?(sonnet|haiku)"))
            return "deepseek-v4-flash";

        return id;
    }

    public static ModelConfig Resolve(string? model)
    {
        var id = Canonicalize(model);
        return Configs.TryGetValue(id, out var cfg) ? cfg : Configs[DefaultModelId];
    }

    public static bool IsKnown(string? model) => Configs.ContainsKey(Canonicalize(model));

    public static bool IsSupported(string? model) => Resolve(model).Supported;

    public static IReadOnlyList<string> SupportedIds =>
        Configs.Where(kv => kv.Value.Supported).Select(kv => kv.Key).ToList();
}
