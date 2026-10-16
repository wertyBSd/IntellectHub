using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntellectHub;

internal sealed class AccountConfig
{
    public string? Name { get; set; }
    public string? Token { get; set; }
    public string? Cookie { get; set; }
    [JsonPropertyName("hif_dliq")] public string? HifDliq { get; set; }
    [JsonPropertyName("hif_leim")] public string? HifLeim { get; set; }
    public string? WasmUrl { get; set; }
    public bool? Enabled { get; set; }
}

internal sealed class Account
{
    public required string Id { get; init; }
    public required string File { get; set; }
    public required AccountConfig Config { get; set; }

    public long CooldownUntil;
    public string? CooldownReason;
    public int Failures;
    public int TotalFailures;
    public string? LastError;
    public long LastSuccessAt;
    public long LastUsedAt;
    public bool AdminDisabled;

    public bool HasCredentials => !string.IsNullOrEmpty(Config.Token) && !string.IsNullOrEmpty(Config.Cookie);
    public bool Enabled => Config.Enabled != false && !AdminDisabled;
    public bool CanServe(long now) => HasCredentials && Enabled && CooldownUntil <= now;
}

internal sealed class AccountsFile
{
    public required IReadOnlyList<Account> List;
    public required IReadOnlyList<string> Added;
    public required IReadOnlyList<string> Kept;
    public required IReadOnlyList<string> Removed;
    public required IReadOnlyList<string> Relogged;
}

internal sealed class AccountPool
{
    private readonly List<Account> _accounts = new();
    private readonly object _gate = new();
    private readonly HashSet<string> _busy = new();

    private readonly string _authPath;
    private readonly string _accountsDir;

    /// <summary>Cooldown applied when DeepSeek gives no Retry-After to follow.</summary>
    public long CooldownMs { get; set; } = DefaultCooldownMs;

    public const long DefaultCooldownMs = 10 * 60 * 1000;

    public AccountPool(string root)
    {
        _authPath = Path.Combine(root, "deepseek-auth.json");
        _accountsDir = Path.Combine(root, "accounts");
    }

    public IReadOnlyList<Account> All
    {
        get { lock (_gate) return _accounts.ToList(); }
    }

    public Account? Primary
    {
        get { lock (_gate) return _accounts.FirstOrDefault(); }
    }

    private IReadOnlyList<string> DiscoverPaths()
    {
        var files = new List<string>();
        if (File.Exists(_authPath)) files.Add(Path.GetFullPath(_authPath));
        try
        {
            if (Directory.Exists(_accountsDir))
            {
                foreach (var name in Directory.GetFiles(_accountsDir, "*.json").OrderBy(n => n, StringComparer.Ordinal))
                    files.Add(Path.GetFullPath(name));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"accounts dir: {ex.Message}");
        }
        if (files.Count == 0) files.Add(_authPath);
        return files.Distinct().ToList();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static string UniqueId(string file, HashSet<string> taken)
    {
        var baseName = Path.GetFileNameWithoutExtension(file);
        var cleaned = new string(baseName.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray());
        if (string.IsNullOrEmpty(cleaned)) cleaned = "account";
        var id = cleaned;
        var n = 2;
        while (taken.Contains(id)) id = $"{cleaned}_{n++}";
        return id;
    }

    public AccountsFile Reload()
    {
        var paths = DiscoverPaths();
        var loaded = new List<(string File, AccountConfig Config)>();
        foreach (var file in paths)
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<AccountConfig>(File.ReadAllText(file), JsonOptions)
                          ?? throw new InvalidOperationException("empty");
                loaded.Add((file, cfg));
            }
            catch (JsonException)
            {
                AppLog.Error($"auth {Path.GetFileName(file)}: invalid JSON");
            }
            catch (IOException ex)
            {
                AppLog.Error($"auth {Path.GetFileName(file)}: {ex.GetType().Name}");
            }
        }

        lock (_gate)
        {
            var previousByFile = _accounts
                .GroupBy(a => Path.GetFullPath(a.File))
                .ToDictionary(g => g.Key, g => g.First());
            var taken = new HashSet<string>();
            var result = new List<Account>();
            var added = new List<string>();
            var kept = new List<string>();
            var relogged = new List<string>();

            foreach (var (file, config) in loaded)
            {
                var full = Path.GetFullPath(file);
                if (previousByFile.TryGetValue(full, out var prev) && !taken.Contains(prev.Id))
                {
                    previousByFile.Remove(full);
                    taken.Add(prev.Id);
                    var changed = prev.Config.Token != config.Token || prev.Config.Cookie != config.Cookie;
                    prev.File = file;
                    prev.Config = config;
                    if (changed)
                    {
                        prev.CooldownUntil = 0;
                        prev.CooldownReason = null;
                        prev.Failures = 0;
                        prev.LastError = null;
                        relogged.Add(prev.Id);
                    }
                    result.Add(prev);
                    kept.Add(prev.Id);
                }
                else
                {
                    var id = UniqueId(file, taken);
                    taken.Add(id);
                    result.Add(new Account { Id = id, File = file, Config = config });
                    added.Add(id);
                }
            }

            var removed = previousByFile.Values.Select(a => a.Id).ToList();
            _accounts.Clear();
            _accounts.AddRange(result);
            _busy.RemoveWhere(id => _accounts.All(a => a.Id != id));

            return new AccountsFile { List = result, Added = added, Kept = kept, Removed = removed, Relogged = relogged };
        }
    }

    /// <summary>Reports only real changes, so a steady-state reload stays silent.</summary>
    internal void LogReload(AccountsFile file)
    {
        if (file.Added.Count == 0 && file.Removed.Count == 0 && file.Relogged.Count == 0) return;

        var parts = new List<string>();
        if (file.Added.Count > 0) parts.Add($"добавлен {file.Added.Count}");
        if (file.Removed.Count > 0) parts.Add($"удалён {file.Removed.Count}");
        if (file.Relogged.Count > 0) parts.Add($"вход обновлён {file.Relogged.Count}");

        var message = $"аккаунты перечитаны: {string.Join(", ", parts)} · всего {file.List.Count}";
        if (file.Relogged.Count > 0) AppLog.Ok(message);
        else AppLog.Info(message);
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public (int Ready, int Total) Snapshot()
    {
        var now = Now();
        lock (_gate) return (_accounts.Count(a => a.CanServe(now)), _accounts.Count);
    }

    /// <summary>Milliseconds until the first sidelined login frees up, or 0.</summary>
    public long NextCooldownMs()
    {
        var now = Now();
        lock (_gate)
        {
            var next = _accounts
                .Where(a => a.CooldownUntil > now)
                .Select(a => a.CooldownUntil - now)
                .DefaultIfEmpty(0)
                .Min();
            return next;
        }
    }

    public void MarkRateLimited(Account account, long? retryAfterMs)
    {
        var ms = retryAfterMs is > 0 ? retryAfterMs.Value : CooldownMs;
        lock (_gate)
        {
            // Never shorten a cooldown that is already running: a late in-flight
            // response must not undo a penalty another request already paid.
            account.CooldownUntil = Math.Max(account.CooldownUntil, Now() + ms);
            account.CooldownReason = "rate_limit";
        }
    }

    public void MarkAuthFailed(Account account)
    {
        lock (_gate)
        {
            account.CooldownUntil = Math.Max(account.CooldownUntil, Now() + CooldownMs);
            account.CooldownReason = "auth";
        }
    }

    public void MarkSuccess(Account account)
    {
        lock (_gate)
        {
            // The cooldown is deliberately left alone: a late reply from a request
            // that started before the penalty must not erase that penalty.
            account.Failures = 0;
            account.LastSuccessAt = Now();
            account.LastUsedAt = Now();
        }
    }

    public bool TryAcquire(Account account)
    {
        lock (_gate)
        {
            if (_busy.Contains(account.Id)) return false;
            _busy.Add(account.Id);
            return true;
        }
    }

    public void Release(Account account)
    {
        lock (_gate) _busy.Remove(account.Id);
    }

    public Account? Select(Account? sticky)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_gate)
        {
            if (sticky is not null)
            {
                var current = _accounts.FirstOrDefault(a => a.Id == sticky.Id);
                if (current is not null && current.CanServe(now) && !_busy.Contains(current.Id))
                    return current;
            }

            var ready = _accounts.Where(a => a.CanServe(now)).ToList();
            if (ready.Count == 0) return null;

            var idle = ready.Where(a => !_busy.Contains(a.Id)).ToList();
            var pool = idle.Count > 0 ? idle : ready;
            return pool.OrderBy(a => a.LastUsedAt).ThenBy(a => a.Id, StringComparer.Ordinal).First();
        }
    }
}
