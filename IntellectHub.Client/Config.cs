using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntellectHub.Client;

/// <summary>
/// A gateway the client knows by name. The URL is what connects, but people refer
/// to the thing they work with, not the socket it listens on.
/// </summary>
public sealed class ServerEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public sealed class ClientConfig
{
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "http://127.0.0.1:9656";

    /// <summary>Gateways this client can switch between by name.</summary>
    [JsonPropertyName("servers")]
    public List<ServerEntry> Servers { get; set; } = [];

    /// <summary>
    /// Which of <see cref="Servers"/> is in use. Empty means the address in
    /// <see cref="BaseUrl"/> was typed by hand and is not one of the named ones.
    /// </summary>
    [JsonPropertyName("server")]
    public string ServerName { get; set; } = "";

    /// <summary>What to call the current server. Never empty.</summary>
    [JsonIgnore]
    public string ServerLabel => string.IsNullOrWhiteSpace(ServerName) ? "custom" : ServerName;

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

    [JsonIgnore]
    public static string ConfigDir { get; internal set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IntellectHub");

    [JsonIgnore]
    public static string ConfigPath { get; internal set; } = Path.Combine(ConfigDir, "client.json");

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
