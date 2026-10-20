using System.Text.Json;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>Stage 1: throttle detection, failover, queueing, deadline, load shedding.</summary>
public sealed class ThrottlingTests
{
    // ---- throttle detection ----------------------------------------------------

    [Theory]
    [InlineData("{\"code\":\"rate_limit\"}")]
    [InlineData("Too many requests, slow down")]
    [InlineData("Слишком частые сообщения, попробуйте позже")]
    [InlineData("запросы слишком частые")]
    [InlineData("请求过于频繁，请稍后再试")]
    [InlineData("请求次数过多")]
    public void RateLimit_RecognisesLocalizedThrottleNotices(string body)
    {
        Assert.True(RateLimit.IsSignal(body));
    }

    [Theory]
    [InlineData("Here is your game")]
    [InlineData("generate limit reached for the free tier")]
    [InlineData("moderate limit of 5 requests")]
    [InlineData("")]
    [InlineData(null)]
    public void RateLimit_DoesNotFireOnOrdinaryReplies(string? body)
    {
        Assert.False(RateLimit.IsSignal(body));
    }

    [Fact]
    public void RateLimit_DoesNotFireInsideAWord()
    {
        // "rate limit" must not match inside "generate limit".
        Assert.False(RateLimit.IsSignal("generate limit of tokens"));
    }

    [Fact]
    public void ContextTooLong_IsNotConfusedWithThrottling()
    {
        Assert.True(RateLimit.IsContextTooLong("context length exceeded, maximum context is 64000"));
        Assert.True(RateLimit.IsContextTooLong("содержание слишком длинное"));
        Assert.True(RateLimit.IsContextTooLong("内容过长"));

        // A sidelined login is not an oversized prompt: compacting for one would
        // silently shrink the conversation while the real problem is the cooldown.
        Assert.False(RateLimit.IsContextTooLong("Слишком частые сообщения"));
        Assert.False(RateLimit.IsContextTooLong("too many requests"));
    }

    [Fact]
    public void AThrottleHintInsideA200Stream_IsDetected()
    {
        var acc = new SseAccumulator();
        acc.Consume("event: hint");
        acc.Consume("data: {\"finish_reason\":\"rate_limit_reached\",\"content\":\"Слишком частые сообщения\"}");
        acc.Consume("data: {\"p\":\"response/finish_reason\",\"v\":\"rate_limit_reached\"}");

        Assert.True(acc.RateLimited);
        Assert.Contains("частые", acc.RateLimitDetail);
    }

    [Fact]
    public void AnOrdinaryStreamIsNotFlaggedAsThrottled()
    {
        var acc = new SseAccumulator();
        acc.Consume("event: response");
        acc.Consume("data: {\"response_message_id\":\"m1\"}");
        acc.Consume("data: {\"p\":\"response/content\",\"v\":\"hi\"}");
        acc.Consume("data: {\"p\":\"response/finish_reason\",\"v\":\"stop\"}");

        Assert.False(acc.RateLimited);
        Assert.Equal("hi", acc.Content);
        Assert.Equal("m1", acc.MessageId);
    }

    // ---- settings ---------------------------------------------------------------

    [Fact]
    public void Normalize_KeepsSaneValues()
    {
        var config = new AppConfig
        {
            UpstreamTimeoutSeconds = 45,
            RequestDeadlineSeconds = 90,
            LockWaitSeconds = 30,
            AccountCooldownSeconds = 120,
            MaxConcurrent = 8,
            MinRequestIntervalMs = 1_500,
        }.Normalize();

        Assert.Equal(45, config.UpstreamTimeoutSeconds);
        Assert.Equal(90, config.RequestDeadlineSeconds);
        Assert.Equal(30, config.LockWaitSeconds);
        Assert.Equal(120, config.AccountCooldownSeconds);
        Assert.Equal(8, config.MaxConcurrent);
        Assert.Equal(1_500, config.MinRequestIntervalMs);
    }

    [Fact]
    public void Normalize_RepairsOutOfRangeValues()
    {
        var config = new AppConfig
        {
            Port = 0,
            UpstreamTimeoutSeconds = 0,
            RequestDeadlineSeconds = -5,
            LockWaitSeconds = 9_999,
            MaxConcurrent = 0,
            MinRequestIntervalMs = -1,
        }.Normalize();

        Assert.Equal(9656, config.Port);
        Assert.Equal(5, config.UpstreamTimeoutSeconds);
        Assert.Equal(5, config.RequestDeadlineSeconds);
        Assert.Equal(1, config.MaxConcurrent);
        Assert.Equal(0, config.MinRequestIntervalMs);
    }

    [Fact]
    public void PacingDefaultsToTwentyFiveSeconds()
    {
        Assert.Equal(25_000, new AppConfig().MinRequestIntervalMs);
    }

    [Fact]
    public void AnOlderConfigIsMovedOntoTheNewPacingDefault()
    {
        // Configs written before pacing was enabled stored 0 only because that was
        // the default. Keeping it would leave the user unprotected after upgrade.
        var stored = new AppConfig { ConfigVersion = 0, MinRequestIntervalMs = 0 };
        stored.Upgrade();

        Assert.Equal(AppConfig.DefaultMinRequestIntervalMs, stored.MinRequestIntervalMs);
        Assert.Equal(AppConfig.CurrentConfigVersion, stored.ConfigVersion);
    }

    [Fact]
    public void AnExplicitZeroIsHonouredOnACurrentConfig()
    {
        // 0 is a real choice on a current config: "pacing off".
        var stored = new AppConfig
        {
            ConfigVersion = AppConfig.CurrentConfigVersion,
            MinRequestIntervalMs = 0,
        };
        stored.Upgrade();

        Assert.Equal(0, stored.MinRequestIntervalMs);
    }

    [Fact]
    public void LockWait_CannotOutliveTheRequestDeadline()
    {
        var config = new AppConfig { RequestDeadlineSeconds = 60, LockWaitSeconds = 600 }.Normalize();

        Assert.Equal(60, config.LockWaitSeconds);
        Assert.Equal(60, config.RequestDeadlineSeconds);
    }

    // ---- account pool -----------------------------------------------------------

    [Fact]
    public void Pool_ReportsWhenTheFirstAccountFreesUp()
    {
        using var temp = new TempAccountPool(("a", 300), ("b", 30));
        var pool = temp.Pool;
        pool.Reload();

        var wait = pool.NextCooldownMs();

        Assert.InRange(wait, 25_000, 30_000);
    }

    [Fact]
    public void Pool_NeverShortensAnExistingCooldown()
    {
        using var temp = new TempAccountPool(("a", 600));
        var pool = temp.Pool;
        pool.Reload();
        var account = pool.All[0];

        pool.MarkRateLimited(account, 1_000);
        var first = account.CooldownUntil;
        pool.MarkRateLimited(account, 1_000);

        // A late in-flight response must not undo a penalty already paid.
        Assert.Equal(first, account.CooldownUntil);
    }

    [Fact]
    public void Pool_SuccessDoesNotEraseAPenalty()
    {
        using var temp = new TempAccountPool(("a", 600));
        var pool = temp.Pool;
        pool.Reload();
        var account = pool.All[0];

        pool.MarkRateLimited(account, null);
        var cooldown = account.CooldownUntil;
        pool.MarkSuccess(account);

        Assert.Equal(cooldown, account.CooldownUntil);
        Assert.Equal(0, account.Failures);
    }

    [Fact]
    public void Pool_HonoursTheConfiguredCooldown()
    {
        using var temp = new TempAccountPool(("a", 0));
        var pool = temp.Pool;
        pool.CooldownMs = 42_000;
        var account = pool.All[0];

        pool.MarkRateLimited(account, null);

        Assert.InRange(pool.NextCooldownMs(), 40_000, 42_000);
        Assert.Equal("rate_limit", account.CooldownReason);
    }
}

/// <summary>A pool over throwaway auth files, so tests never touch the real ones.</summary>
internal sealed class TempAccountPool : IDisposable
{
    private readonly string _root;

    public TempAccountPool(params (string Id, int CooldownSeconds)[] accounts)
    {
        _root = Path.Combine(Path.GetTempPath(), "intellecthub-tests", Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(_root, "accounts");
        Directory.CreateDirectory(dir);

        foreach (var (id, _) in accounts)
        {
            File.WriteAllText(Path.Combine(dir, id + ".json"), JsonSerializer.Serialize(new
            {
                token = "tok_" + id,
                cookie = "ds_session_id=" + id,
            }));
        }

        Pool = new AccountPool(_root) { CooldownMs = 600_000 };
        Pool.Reload();

        foreach (var (id, cooldownSeconds) in accounts)
        {
            Pool.All.First(a => a.Id == id).MarkCooldown(
                cooldownSeconds > 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + cooldownSeconds * 1000L : 0,
                cooldownSeconds > 0 ? "test" : null);
        }
    }

    public AccountPool Pool { get; }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}