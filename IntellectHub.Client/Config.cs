using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntellectHub.Client;

/// <summary>
/// Wire protocol used to talk to a model provider.
/// </summary>
public enum ProviderKind
{
    /// <summary>Anthropic-compatible /v1/messages with messages/tool_use blocks.</summary>
    Anthropic,
    /// <summary>OpenAI-compatible /v1/chat/completions with tool-calling.</summary>
    OpenAI,
    /// <summary>Cloudflare R2 S3-compatible object storage.</summary>
    CloudflareR2,
}

/// <summary>
/// How the credential for a connection is presented to the provider.
/// </summary>
public enum AuthStyle
{
    /// <summary>Authorization: Bearer &lt;key&gt; — OpenAI, OpenRouter, DeepSeek, most gateways.</summary>
    Bearer,
    /// <summary>x-api-key: &lt;key&gt; plus anthropic-version — Anthropic.</summary>
    AnthropicKey,
    /// <summary>x-goog-api-key: &lt;key&gt; — Gemini.</summary>
    GoogleApiKey,
    /// <summary>No credential is sent.</summary>
    None,
}

/// <summary>
/// A named connection to a model provider or storage backend.
/// </summary>
public sealed class ConnectionEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("provider")] public ProviderKind Provider { get; set; } = ProviderKind.Anthropic;
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";

    /// <summary>How the API key from secrets.json is presented.</summary>
    [JsonPropertyName("auth")] public AuthStyle Auth { get; set; } = AuthStyle.Bearer;

    /// <summary>
    /// Where the credential for this connection lives in secrets.json. Falls back to
    /// the conventional name so a hand-written entry still finds its key.
    /// </summary>
    [JsonIgnore]
    public string CredentialKey => string.IsNullOrWhiteSpace(SecretKey) ? $"apiKey:{Name}" : SecretKey;

    /// <summary>Headers the provider expects on every request, beyond the credential.</summary>
    [JsonPropertyName("headers")] public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A copy that can be edited without touching the shared preset it came from.
    /// </summary>
    public ConnectionEntry Copy() => new()
    {
        Name = Name,
        Provider = Provider,
        Url = Url,
        Model = Model,
        Auth = Auth,
        SecretKey = SecretKey,
        Headers = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>
    /// Where the credential lives in secrets.json. Kept separate from the display
    /// name so a connection can be renamed without orphaning its key.
    /// </summary>
    [JsonPropertyName("secretKey")] public string SecretKey { get; set; } = "";
}

public sealed class ServerEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public sealed class ClientConfig
{
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "http://127.0.0.1:9656";

    /// <summary>Named provider connections (OpenAI/OpenRouter/Google/DeepSeek/Cloudflare R2/etc.).</summary>
    [JsonPropertyName("connections")]
    public List<ConnectionEntry> Connections { get; set; } = [];

    /// <summary>Gateways this client can switch between by name.</summary>
    [JsonPropertyName("servers")]
    public List<ServerEntry> Servers { get; set; } = [];

    /// <summary>
    /// Which named connection is in use. Empty means BaseUrl was typed by hand and is not
    /// one of the known named connections. This preserves the connection context across
    /// reconnections and the /connect command.
    /// </summary>
    [JsonPropertyName("connection")]
    public string ConnectionName { get; set; } = "";

    /// <summary>Static catalog of known provider presets.</summary>
    public static class Providers
    {
        /// <summary>Anthropic API (Claude 3.5). Requires X-API-KEY header.</summary>
        public static ConnectionEntry Anthropic() => new()
        {
            Name = "anthropic",
            Provider = ProviderKind.Anthropic,
            Url = "https://api.anthropic.com",
            Model = "claude-3-5-sonnet-latest",
            Auth = AuthStyle.AnthropicKey,
            SecretKey = "apiKey:anthropic",
        };

        /// <summary>OpenRouter's OpenAI-compatible gateway. Supports many models, HTTP-Referer, X-Title.</summary>
        public static ConnectionEntry OpenRouter() => new()
        {
            Name = "openrouter",
            Provider = ProviderKind.OpenAI,
            Url = "https://openrouter.ai/api/v1",
            Model = "deepseek/deepseek-chat-v3-0324",
            Auth = AuthStyle.Bearer,
            SecretKey = "apiKey:openrouter",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["HTTP-Referer"] = "https://intellecthub.local",
                ["X-Title"] = "IntellectHub Client",
            },
        };

        /// <summary>Google Gemini via OpenAI-compatible endpoint. Provides reasonable free tier.</summary>
        public static ConnectionEntry Google() => new()
        {
            Name = "google",
            Provider = ProviderKind.OpenAI,
            Url = "https://generativelanguage.googleapis.com/v1beta/openai",
            Model = "gemini-2.0-flash",
            Auth = AuthStyle.GoogleApiKey,
            SecretKey = "apiKey:google",
        };

        /// <summary>DeepSeek API (OpenAI-compatible). Affordable, good for chat agents.</summary>
        public static ConnectionEntry DeepSeek() => new()
        {
            Name = "deepseek",
            Provider = ProviderKind.OpenAI,
            Url = "https://api.deepseek.com",
            Model = "deepseek-chat",
            Auth = AuthStyle.Bearer,
            SecretKey = "apiKey:deepseek",
        };

        /// <summary>Cloudflare R2 storage (S3-compatible). Only storage, no chat models.</summary>
        public static ConnectionEntry CloudflareR2() => new()
        {
            Name = "cloudflare-r2",
            Provider = ProviderKind.CloudflareR2,
            Url = "https://e5f77bc8451557f4552bdfd0dd06977f.r2.cloudflarestorage.com",
            Model = "",
            Auth = AuthStyle.None,

            // No SecretKey: the storage credentials are a pair read from the
            // environment or from the r2:* entries in secrets.json, not a single
            // key stored under this connection's name.
        };

        /// <summary>Every built-in preset, in the order the picker shows them.</summary>
        public static IReadOnlyList<ConnectionEntry> Presets { get; } =
        [
            Anthropic(),
            OpenRouter(),
            Google(),
            DeepSeek(),
            CloudflareR2(),
        ];

        /// <summary>Finds a preset by name, case-insensitively.</summary>
        public static ConnectionEntry? Find(string name) =>
            Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Which of <see cref="Servers"/> is in use. Empty means the address in
    /// <see cref="BaseUrl"/> was typed by hand and is not one of the named ones.
    /// </summary>
    [JsonPropertyName("server")]
    public string ServerName { get; set; } = "";

    /// <summary>What to call the current server. Never empty.</summary>
    [JsonIgnore]
    public string ServerLabel =>
        !string.IsNullOrWhiteSpace(ServerName) ? ServerName
        : !string.IsNullOrWhiteSpace(ConnectionName) ? ConnectionName
        : "custom";

    /// <summary>
    /// Every connection the client can switch to: the built-in presets plus any the
    /// user saved. A saved entry of the same name keeps the preset's provider
    /// semantics but takes over the address and the model, so a hand-written
    /// "openrouter" still sends the headers OpenRouter asks for.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ConnectionEntry> AllConnections
    {
        get
        {
            var all = new List<ConnectionEntry>();

            foreach (var preset in Providers.Presets)
            {
                var saved = Connections.FirstOrDefault(c =>
                    string.Equals(c.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
                all.Add(saved is null ? preset.Copy() : Merge(preset, saved));
            }

            foreach (var saved in Connections)
            {
                if (all.Any(c => string.Equals(c.Name, saved.Name, StringComparison.OrdinalIgnoreCase))) continue;
                all.Add(saved);
            }

            return all;
        }
    }

    /// <summary>
    /// A saved entry over a preset: the provider, the credential style and the
    /// headers come from the preset, the address and the model from the saved entry.
    /// </summary>
    private static ConnectionEntry Merge(ConnectionEntry preset, ConnectionEntry saved) => new()
    {
        Name = preset.Name,
        Provider = preset.Provider,
        Auth = preset.Auth,
        Headers = new Dictionary<string, string>(preset.Headers, StringComparer.OrdinalIgnoreCase),
        SecretKey = string.IsNullOrWhiteSpace(saved.SecretKey) ? preset.SecretKey : saved.SecretKey,
        Url = string.IsNullOrWhiteSpace(saved.Url) ? preset.Url : saved.Url,
        Model = string.IsNullOrWhiteSpace(saved.Model) ? preset.Model : saved.Model,
    };

    /// <summary>
    /// The connection in use, or null when the address was typed by hand and belongs
    /// to no named connection.
    /// </summary>
    [JsonIgnore]
    public ConnectionEntry? ActiveConnection => AllConnections.FirstOrDefault(c =>
        string.Equals(c.Name, ConnectionName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The storage connection, whether or not it is the one in use: uploading to R2
    /// should not require switching the chat provider over to it first.
    /// </summary>
    [JsonIgnore]
    public ConnectionEntry? StorageConnection => AllConnections.FirstOrDefault(c =>
        c.Provider == ProviderKind.CloudflareR2);

    /// <summary>
    /// Points the client at a named connection and adopts its address and model.
    /// The server list is cleared: the two name spaces are alternatives, and leaving
    /// both set would label the current address with a name that no longer describes it.
    /// </summary>
    public void ApplyConnection(ConnectionEntry connection)
    {
        ConnectionName = connection.Name;
        ServerName = "";

        if (!string.IsNullOrWhiteSpace(connection.Url))
        {
            BaseUrl = connection.Url;
            _envBaseUrl = false;
        }

        if (!string.IsNullOrWhiteSpace(connection.Model))
        {
            Model = connection.Model;
            _envModel = false;
        }
    }

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = "deepseek-v4-flash";

    [JsonPropertyName("system")]
    public string System { get; set; } =
        "You are IntellectHub, a concise and helpful assistant. When the user asks you to create, generate, write, or save a file (code, documentation, markdown, config, etc.), ALWAYS use the 'write' tool with the full file content in the 'content' argument. Do not output file content as text — use the tool.\n\n" +
        "TASK DECOMPOSITION: When the user gives you a complex task (creating a large file, multiple files, or anything requiring many steps), do NOT try to do it all at once. Instead:\n\n" +
        "1. First, output a JSON plan with subtasks:\n" +
        "   {\"subtasks\":[{\"title\":\"Section 1: Introduction\",\"description\":\"Write the introduction section\"},{\"title\":\"Section 2: Setup\",\"description\":\"Describe environment setup\"}]}\n\n" +
        "2. Then wait — the client will ask you to execute each subtask one by one.\n\n" +
        "3. For each subtask, use write/edit tools to write ONLY that part of the content. Do not try to write the entire file at once.\n\n" +
        "4. When a subtask is complete, reply with plain text (no tool calls) and the client will move to the next subtask.\n\n" +
        "If you cannot use tools or the task is too big for one turn, return the JSON plan with subtasks — do not reply with an empty noop call.";

    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; set; } = 4096;

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 1.0;

    [JsonPropertyName("project")]
    public string? Project { get; set; }

    /// <summary>
    /// Projects switched to recently, newest first. The project picker is built
    /// from this so returning to a workspace is one keypress instead of a path.
    /// </summary>
    [JsonPropertyName("recentProjects")]
    public List<string> RecentProjects { get; set; } = [];

    /// <summary>
    /// How much of the agent's work runs without stopping. Stored as text so the
    /// value stays readable in client.json.
    /// </summary>
    [JsonPropertyName("approvals")]
    public string ApprovalMode { get; set; } = nameof(Approvals.Ask);

    [JsonIgnore]
    public Approvals Approvals => ToolRisk.Parse(ApprovalMode);

    /// <summary>
    /// Включает перепроверку выполнения подзадач. Когда включено (по умолчанию),
    /// клиент проверяет, что модель действительно записала содержимое в файл,
    /// а не просто вывела текст.
    /// </summary>
    [JsonPropertyName("selfCheck")]
    public bool SelfCheck { get; set; } = true;

    [JsonIgnore]
    public static string ConfigDir { get; internal set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IntellectHub");

    [JsonIgnore]
    public static string ConfigPath { get; internal set; } = Path.Combine(ConfigDir, "client.json");

    /// <summary>
    /// Where credentials live, kept out of client.json so the readable configuration
    /// can be shared without handing over the keys.
    /// </summary>
    [JsonIgnore]
    public static string SecretsPath { get; internal set; } = Path.Combine(ConfigDir, "secrets.json");

    /// <summary>Every stored credential, by its key inside secrets.json.</summary>
    public static Dictionary<string, string> LoadSecrets()
    {
        if (!File.Exists(SecretsPath)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SecretsPath))
                   ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            // A corrupt secrets file must not take the client down: the keys are
            // re-enterable, and refusing to start would leave no way to fix it.
            return new Dictionary<string, string>();
        }
    }

    /// <summary>One credential, or null when it was never set.</summary>
    public static string? GetSecret(string key) =>
        LoadSecrets().TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>Stores one credential, leaving the others untouched.</summary>
    public static void SaveSecret(string key, string value)
    {
        var secrets = LoadSecrets();
        secrets[key] = value;

        Directory.CreateDirectory(Path.GetDirectoryName(SecretsPath) ?? ConfigDir);
        File.WriteAllText(SecretsPath, JsonSerializer.Serialize(secrets, Json));
    }

    /// <summary>Removes one credential. Returns false when there was nothing to remove.</summary>
    public static bool DeleteSecret(string key)
    {
        var secrets = LoadSecrets();
        if (!secrets.Remove(key)) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(SecretsPath) ?? ConfigDir);
        File.WriteAllText(SecretsPath, JsonSerializer.Serialize(secrets, Json));
        return true;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Environment variables are per-process overrides. Saving must not turn them
    // into the stored configuration: one run with IH_BASE_URL set would otherwise
    // rewrite the file and leave the client pointing at that address forever.
    private bool _envBaseUrl;
    private string? _envBaseUrlValue;
    private string? _storedBaseUrl;
    private bool _envApiKey;
    private string? _envApiKeyValue;
    private string? _storedApiKey;
    private bool _envModel;
    private string? _envModelValue;
    private string? _storedModel;

    public static ClientConfig Load()
    {
        ClientConfig config = new();
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<ClientConfig>(json);
                if (loaded is not null)
                {
                    config = loaded;
                    // A hand-edited file can carry "servers": null. Treat that as an
                    // empty list rather than letting the first switch throw.
                    config.Servers ??= [];
                    config.RecentProjects ??= [];
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"warning: could not read {ConfigPath}: {ex.Message}");
            }
        }

        // Auto-seed the built-in presets so /connect openrouter works even before
        // the user has saved anything. A saved entry of the same name takes
        // precedence over the preset default.
        var seeded = new HashSet<string>(config.Connections.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var preset in Providers.Presets)
            if (!seeded.Contains(preset.Name)) config.Connections.Add(preset.Copy());

        var envUrl = Environment.GetEnvironmentVariable("IH_BASE_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
        {
            config._storedBaseUrl = config.BaseUrl;
            config._envBaseUrlValue = envUrl;
            config._envBaseUrl = true;
            config.BaseUrl = envUrl;
        }

        var envKey = Environment.GetEnvironmentVariable("IH_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            config._storedApiKey = config.ApiKey;
            config._envApiKeyValue = envKey;
            config._envApiKey = true;
            config.ApiKey = envKey;
        }

        var envModel = Environment.GetEnvironmentVariable("IH_MODEL");
        if (!string.IsNullOrWhiteSpace(envModel))
        {
            config._storedModel = config.Model;
            config._envModelValue = envModel;
            config._envModel = true;
            config.Model = envModel;
        }

        return config;
    }

    /// <summary>Applies a command-line address, which outranks the environment for this run.</summary>
    public void OverrideBaseUrl(string url)
    {
        BaseUrl = url;
        _envBaseUrl = false;
    }

    /// <summary>Applies a command-line model, which outranks the environment for this run.</summary>
    public void OverrideModel(string model)
    {
        Model = model;
        _envModel = false;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);

        // Keep the values that came from the file for anything still sitting at an
        // environment override. A value changed at runtime is persisted as usual.
        var stored = (ClientConfig)MemberwiseClone();
        if (_envBaseUrl && string.Equals(BaseUrl, _envBaseUrlValue, StringComparison.Ordinal))
            stored.BaseUrl = _storedBaseUrl ?? stored.BaseUrl;
        if (_envApiKey && string.Equals(ApiKey, _envApiKeyValue, StringComparison.Ordinal))
            stored.ApiKey = _storedApiKey;
        if (_envModel && string.Equals(Model, _envModelValue, StringComparison.Ordinal))
            stored.Model = _storedModel ?? stored.Model;

        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(stored, Json));
    }
}
