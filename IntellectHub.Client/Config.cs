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
    public string System { get; set; } = "You are IntellectHub, a concise and helpful assistant.";

    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; set; } = 4096;

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 1.0;

    [JsonPropertyName("project")]
    public string? Project { get; set; }

    /// <summary>
    /// How much of the agent's work runs without stopping. Stored as text so the
    /// value stays readable in client.json.
    /// </summary>
    [JsonPropertyName("approvals")]
    public string ApprovalMode { get; set; } = nameof(Approvals.Ask);

    [JsonIgnore]
    public Approvals Approvals => ToolRisk.Parse(ApprovalMode);

    [JsonIgnore]
    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IntellectHub");

    [JsonIgnore]
    public static string ConfigPath => Path.Combine(ConfigDir, "client.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ClientConfig Load()
    {
        ClientConfig config = new();
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<ClientConfig>(json);
                if (loaded is not null) config = loaded;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"warning: could not read {ConfigPath}: {ex.Message}");
            }
        }

        var envUrl = Environment.GetEnvironmentVariable("IH_BASE_URL");
        if (!string.IsNullOrWhiteSpace(envUrl)) config.BaseUrl = envUrl;
        var envKey = Environment.GetEnvironmentVariable("IH_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey)) config.ApiKey = envKey;
        var envModel = Environment.GetEnvironmentVariable("IH_MODEL");
        if (!string.IsNullOrWhiteSpace(envModel)) config.Model = envModel;

        return config;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Json));
    }
}
