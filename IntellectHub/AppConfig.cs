using System.Text.Json;

namespace IntellectHub;

internal sealed class AppConfig
{
    // ---- server ----------------------------------------------------------------
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9656;
    public string ApiKey { get; set; } = string.Empty;
    public string AuthPath { get; set; } = "deepseek-auth.json";
    public bool AutoStartServer { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool AutoScrollLog { get; set; } = true;

    // ---- upstream pacing and deadlines -----------------------------------------
    /// <summary>Wall-clock budget for one attempt against DeepSeek.</summary>
    public int UpstreamTimeoutSeconds { get; set; } = 60;

    /// <summary>Total budget for one inbound request, across every failover hop.</summary>
    public int RequestDeadlineSeconds { get; set; } = 120;

    /// <summary>How long a request may wait for a busy account before giving up.</summary>
    public int LockWaitSeconds { get; set; } = 120;

    /// <summary>Cooldown applied to an account after 429 or 401/403 with no Retry-After.</summary>
    public int AccountCooldownSeconds { get; set; } = 600;

    /// <summary>Upstream calls allowed in flight at once; beyond this the gateway sheds load.</summary>
    public int MaxConcurrent { get; set; } = 24;

    /// <summary>
    /// Minimum spacing between two upstream sends, regardless of account. 0 disables
    /// pacing, which is the behaviour of the original gateway.
    /// </summary>
    public int MinRequestIntervalMs { get; set; } = DefaultMinRequestIntervalMs;

    public const int DefaultMinRequestIntervalMs = 25_000;

    /// <summary>Bumped when a stored setting must be re-based on a new default.</summary>
    public int ConfigVersion { get; set; } = CurrentConfigVersion;

    public const int CurrentConfigVersion = 1;

    // ---- prompt budgets --------------------------------------------------------

    /// <summary>Hard cap on characters sent upstream.</summary>
    public int MaxPromptChars { get; set; } = 80_000;

    /// <summary>How many times one request may re-ask after an empty or oversized reply.</summary>
    public int MaxRetries { get; set; } = 3;

    // ---- validation ------------------------------------------------------------

    public const int MinPromptChars = 16_000;
    public const int MaxPromptCharsCeiling = 2_000_000;

    /// <summary>
    /// Forces every setting into a range the rest of the code can rely on. Values are
    /// clipped to the nearest bound rather than reset to the default, so asking for
    /// 99 concurrent requests yields 512 and not 24.
    /// </summary>
    public AppConfig Normalize()
    {
        Host = string.IsNullOrWhiteSpace(Host) ? "127.0.0.1" : Host.Trim();

        // A port number is not usefully clipped, so a nonsense one falls back.
        if (Port is < 1 or > 65535) Port = 9656;

        UpstreamTimeoutSeconds = Clamp(UpstreamTimeoutSeconds, 5, 600);
        RequestDeadlineSeconds = Clamp(RequestDeadlineSeconds, 5, 1800);
        // A request can never outlive its own deadline.
        LockWaitSeconds = Clamp(LockWaitSeconds, 0, RequestDeadlineSeconds);
        AccountCooldownSeconds = Clamp(AccountCooldownSeconds, 1, 86_400);
        MaxConcurrent = Clamp(MaxConcurrent, 1, 512);
        // 0 is meaningful for both of these: pacing off, retries off.
        MinRequestIntervalMs = Clamp(MinRequestIntervalMs, 0, 600_000);
        MaxPromptChars = Clamp(MaxPromptChars, MinPromptChars, MaxPromptCharsCeiling);
        MaxRetries = Clamp(MaxRetries, 0, 10);

        return this;
    }

    private static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

    // ---- persistence -----------------------------------------------------------

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "IntellectHub",
        "config.json");

    /// <summary>
    /// Re-bases settings whose default changed. A stored 0 means "pacing off" now,
    /// but older configs wrote 0 only because that used to be the default, so those
    /// are moved onto the new default instead of silently keeping it off.
    /// </summary>
    internal void Upgrade()
    {
        if (ConfigVersion >= CurrentConfigVersion) return;

        if (MinRequestIntervalMs == 0)
            MinRequestIntervalMs = DefaultMinRequestIntervalMs;

        ConfigVersion = CurrentConfigVersion;
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Options) ?? new AppConfig();
                config.Upgrade();
                return config.Normalize();
            }
        }
        catch
        {
        }
        return new AppConfig().Normalize();
    }

    public void Save()
    {
        try
        {
            Normalize();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
        }
    }
}