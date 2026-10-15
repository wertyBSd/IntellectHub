using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntellectHub;

internal enum KeyStatus
{
    Ok,
    Unknown,
    Disabled,
    Expired,
    DailyLimit,
    MonthlyLimit,
}

internal sealed class ApiKeyRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("prefix")] public string Prefix { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
    [JsonPropertyName("expiresAt")] public long? ExpiresAt { get; set; }
    [JsonPropertyName("dailyLimit")] public long? DailyLimit { get; set; }
    [JsonPropertyName("monthlyLimit")] public long? MonthlyLimit { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("usedTotal")] public long UsedTotal { get; set; }
    [JsonPropertyName("day")] public string? Day { get; set; }
    [JsonPropertyName("usedToday")] public long UsedToday { get; set; }
    [JsonPropertyName("month")] public string? Month { get; set; }
    [JsonPropertyName("usedMonth")] public long UsedMonth { get; set; }

    public bool IsExpired(long now) => ExpiresAt is { } at && at <= now;
    public bool IsUnlimited => DailyLimit is null && MonthlyLimit is null;
    public bool IsPerpetual => ExpiresAt is null;
}

internal sealed class ApiKeysFile
{
    [JsonPropertyName("keys")] public List<ApiKeyRecord> Keys { get; set; } = new();
}

internal sealed class ApiKeyStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private List<ApiKeyRecord> _keys = new();
    private long _lastWrite;

    public ApiKeyStore(string root)
    {
        _path = Path.Combine(root, "api-keys.json");
        Load();
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool HasKeys { get { lock (_gate) return _keys.Count > 0; } }
    public IReadOnlyList<ApiKeyRecord> All { get { lock (_gate) return _keys.ToList(); } }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string DayKey(long now) => DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime.ToString("yyyy-MM-dd");
    private static string MonthKey(long now) => DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime.ToString("yyyy-MM");

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    private static string GenerateRaw() =>
        "ih_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var file = JsonSerializer.Deserialize<ApiKeysFile>(File.ReadAllText(_path), Options);
            lock (_gate) _keys = file?.Keys ?? new List<ApiKeyRecord>();
        }
        catch (Exception ex)
        {
            AppLog.Error($"api keys load: {ex.Message}");
        }
    }

    private void SaveLocked()
    {
        var now = Now();
        if (now - _lastWrite < 2000) return;
        _lastWrite = now;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new ApiKeysFile { Keys = _keys }, Options));
        }
        catch (Exception ex)
        {
            AppLog.Error($"api keys save: {ex.Message}");
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            _lastWrite = 0;
            SaveLocked();
        }
    }

    public (ApiKeyRecord Record, string Raw) Create(string? name, long? dailyLimit, long? monthlyLimit, long? expiresAt)
    {
        var raw = GenerateRaw();
        var record = new ApiKeyRecord
        {
            Id = "k_" + Guid.NewGuid().ToString("N")[..12],
            Hash = Hash(raw),
            Prefix = raw[..11],
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            CreatedAt = Now(),
            ExpiresAt = expiresAt,
            DailyLimit = dailyLimit is > 0 ? dailyLimit : null,
            MonthlyLimit = monthlyLimit is > 0 ? monthlyLimit : null,
            Enabled = true,
            Day = DayKey(Now()),
            Month = MonthKey(Now()),
        };

        lock (_gate)
        {
            _keys.Add(record);
            _lastWrite = 0;
            SaveLocked();
        }

        return (record, raw);
    }

    private static void Rollover(ApiKeyRecord record, string day, string month)
    {
        if (record.Day != day)
        {
            record.Day = day;
            record.UsedToday = 0;
        }
        if (record.Month != month)
        {
            record.Month = month;
            record.UsedMonth = 0;
        }
    }

    public KeyStatus Validate(string? raw, long now, out ApiKeyRecord? record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(raw)) return KeyStatus.Unknown;

        var hash = Hash(raw.Trim());
        var needle = Encoding.ASCII.GetBytes(hash);

        lock (_gate)
        {
            var found = _keys.FirstOrDefault(k =>
                CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(k.Hash), needle));
            if (found is null) return KeyStatus.Unknown;

            record = found;
            if (!found.Enabled) return KeyStatus.Disabled;
            if (found.IsExpired(now)) return KeyStatus.Expired;

            Rollover(found, DayKey(now), MonthKey(now));

            if (found.DailyLimit is { } dl && found.UsedToday >= dl) return KeyStatus.DailyLimit;
            if (found.MonthlyLimit is { } ml && found.UsedMonth >= ml) return KeyStatus.MonthlyLimit;
            return KeyStatus.Ok;
        }
    }

    public void RecordUse(ApiKeyRecord record, long now)
    {
        lock (_gate)
        {
            Rollover(record, DayKey(now), MonthKey(now));
            record.UsedToday++;
            record.UsedMonth++;
            record.UsedTotal++;
            SaveLocked();
        }
    }

    public bool Revoke(string id)
    {
        lock (_gate)
        {
            var removed = _keys.RemoveAll(k => k.Id == id);
            if (removed > 0)
            {
                _lastWrite = 0;
                SaveLocked();
            }
            return removed > 0;
        }
    }

    public bool SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            var found = _keys.FirstOrDefault(k => k.Id == id);
            if (found is null) return false;
            found.Enabled = enabled;
            _lastWrite = 0;
            SaveLocked();
            return true;
        }
    }

    public static string? ExtractKey(HttpListenerRequest request)
    {
        var auth = request.Headers["Authorization"];
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();

        var header = request.Headers["x-api-key"];
        return string.IsNullOrWhiteSpace(header) ? null : header.Trim();
    }
}
