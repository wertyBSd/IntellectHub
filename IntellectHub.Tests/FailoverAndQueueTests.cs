using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// The queue and failover contract. Several client connections at once is the
/// normal case for an agent harness, so a busy pool must queue and a throttled
/// login must rotate instead of failing the request.
/// </summary>
public sealed class FailoverAndQueueTests
{
    // Pacing is off here: these tests are about failover and queueing, and the real
// default would add a 25s wait to every one of them.
    private static AppConfig Config(int maxConcurrent = 24, int lockWaitSeconds = 5) => new AppConfig
    {
        MaxConcurrent = maxConcurrent,
        LockWaitSeconds = lockWaitSeconds,
        RequestDeadlineSeconds = 30,
        MinRequestIntervalMs = 0,
    }.Normalize();

    private static ChatCompletionRequest Request(string text = "hi") => new()
    {
        Messages = [new ChatMessage { Role = "user", Content = JsonSerializer.SerializeToElement(text) }],
    };

    /// <summary>An upstream that can be told to throttle or hang, per account.</summary>
    private sealed class FakeUpstream : IDeepSeekCompletion
    {
        public ConcurrentBag<string> Calls { get; } = [];

        /// <summary>Session ids the service asked to use, in order.</summary>
        public List<string> Sessions { get; } = [];

        /// <summary>Parent message ids the service supplied, in order.</summary>
        public List<string> Parents { get; } = [];
        public Func<Account, int, CancellationToken, Task<UpstreamResult>>? Handler { get; set; }
        public int CallsFor(string id) => Calls.Count(c => c == id);

        public Task<UpstreamResult> CompleteAsync(Account account, ModelConfig model, string prompt,
            string? chatSessionId, string? parentMessageId, CancellationToken ct)
        {
            Sessions.Add(chatSessionId ?? "");
            Parents.Add(parentMessageId ?? "");
            var attempt = CallsFor(account.Id);
            Calls.Add(account.Id);

            return Handler is null
                ? Task.FromResult(new UpstreamResult("ok from " + account.Id, "", "m", "stop"))
                : Handler(account, attempt, ct);
        }
    }

    [Fact]
    public async Task AThrottledLogin_FailsOverToTheNextOne()
    {
        using var temp = new TempAccountPool(("a", 0), ("b", 0));
        var upstream = new FakeUpstream
        {
            Handler = (account, _, _) => account.Id == "a"
                ? throw new UpstreamRateLimitException("Слишком частые сообщения")
                : Task.FromResult(new UpstreamResult("answer", "", "m", "stop")),
        };

        var service = new ChatCompletionService(temp.Pool, upstream, Config());
        var result = await service.CompleteAsync(Request(), CancellationToken.None);

        Assert.Equal("answer", result.Content);
        Assert.Equal(1, upstream.CallsFor("a"));
        Assert.Equal(1, upstream.CallsFor("b"));

        // The throttled login must be sidelined so later requests skip it.
        Assert.Equal("rate_limit", temp.Pool.All.First(a => a.Id == "a").CooldownReason);
    }

    [Fact]
    public async Task RetryAfter_IsHonouredOverTheConfiguredCooldown()
    {
        using var temp = new TempAccountPool(("a", 0), ("b", 0));
        temp.Pool.CooldownMs = 600_000;

        var upstream = new FakeUpstream
        {
            Handler = (account, _, _) => account.Id == "a"
                ? throw new UpstreamRateLimitException("slow down", retryAfterMs: 30_000)
                : Task.FromResult(new UpstreamResult("answer", "", "m", "stop")),
        };

        var service = new ChatCompletionService(temp.Pool, upstream, Config());
        await service.CompleteAsync(Request(), CancellationToken.None);

        Assert.InRange(temp.Pool.NextCooldownMs(), 25_000, 30_000);
    }

    [Fact]
    public async Task WhenEveryLoginIsThrottled_TheClientGets429WithRetryAfter()
    {
        using var temp = new TempAccountPool(("a", 0), ("b", 0));
        var upstream = new FakeUpstream
        {
            Handler = (_, _, _) => throw new UpstreamRateLimitException("Слишком частые сообщения"),
        };

        var service = new ChatCompletionService(temp.Pool, upstream, Config(lockWaitSeconds: 0));

        var ex = await Assert.ThrowsAsync<UpstreamRateLimitException>(
            () => service.CompleteAsync(Request(), CancellationToken.None));

        Assert.Equal(429, ex.StatusCode);
        Assert.NotNull(ex.RetryAfterMs);
        Assert.Equal("rate_limit", ex.ErrorKind);
    }

    [Fact]
    public async Task AnUnrelatedUpstreamError_DoesNotRotate()
    {
        using var temp = new TempAccountPool(("a", 0), ("b", 0));
        var upstream = new FakeUpstream
        {
            Handler = (_, _, _) => throw new UpstreamException("upstream exploded", 500),
        };

        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        await Assert.ThrowsAsync<UpstreamException>(() => service.CompleteAsync(Request(), CancellationToken.None));

        // A plain 500 says nothing about the login's quota, so rotation would just
        // burn the other account for no reason.
        Assert.Single(upstream.Calls);
    }

    [Fact]
    public async Task ABusyAccount_QueuesInsteadOfFailingImmediately()
    {
        using var temp = new TempAccountPool(("only", 0));
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();

        var upstream = new FakeUpstream
        {
            Handler = async (_, _, ct) =>
            {
                started.TrySetResult();
                await gate.Task;
                return new UpstreamResult("ok", "", "m", "stop");
            },
        };

        var service = new ChatCompletionService(temp.Pool, upstream, Config(lockWaitSeconds: 10));

        var first = service.CompleteAsync(Request(), CancellationToken.None);
        await started.Task;

        // The only login is busy; this request must wait rather than get a 429.
        var second = service.CompleteAsync(Request(), CancellationToken.None);
        await Task.Delay(150);
        Assert.False(second.IsCompleted);

        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.Equal("ok", r.Content));
        Assert.Equal(2, upstream.CallsFor("only"));
    }

    [Fact]
    public async Task AQueueThatCannotFitInTheWaitBudget_FailsCleanly()
    {
        using var temp = new TempAccountPool(("only", 0));
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();

        var upstream = new FakeUpstream
        {
            Handler = async (_, _, ct) =>
            {
                started.TrySetResult();
                await gate.Task;
                return new UpstreamResult("ok", "", "m", "stop");
            },
        };

        var service = new ChatCompletionService(temp.Pool, upstream, Config(lockWaitSeconds: 0));

        var first = service.CompleteAsync(Request(), CancellationToken.None);
        await started.Task;

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => service.CompleteAsync(Request(), CancellationToken.None));

        Assert.Equal(429, ex.StatusCode);
        Assert.Equal("concurrent_chat_blocked", ex.ErrorKind);

        gate.SetResult();
        await first;
    }

    [Fact]
    public async Task LoadBeyondTheConcurrencyCap_IsShed()
    {
        using var temp = new TempAccountPool(("a", 0), ("b", 0), ("c", 0));
        var gate = new TaskCompletionSource();

        var upstream = new FakeUpstream
        {
            Handler = async (_, _, _) =>
            {
                await gate.Task;
                return new UpstreamResult("ok", "", "m", "stop");
            },
        };

        var service = new ChatCompletionService(temp.Pool, upstream, Config(maxConcurrent: 2));

        var inflight = new List<Task<CompletionResult>>();
        for (var i = 0; i < 2; i++) inflight.Add(service.CompleteAsync(Request(), CancellationToken.None));

        await Task.Delay(100);

        // The cap sheds load rather than queueing: unbounded fan-out is what gets
        // a login banned.
        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => service.CompleteAsync(Request(), CancellationToken.None));
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("overloaded", ex.ErrorKind);
        Assert.Equal(2000, ex.RetryAfterMs);

        gate.SetResult();
        await Task.WhenAll(inflight);
    }

    [Fact]
    public async Task Pacing_SpacesConsecutiveUpstreamSends()
    {
        using var temp = new TempAccountPool(("a", 0), ("b", 0));
        var stamps = new List<long>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var upstream = new FakeUpstream
        {
            Handler = (account, _, _) =>
            {
                stamps.Add(clock.ElapsedMilliseconds);
                return Task.FromResult(new UpstreamResult("ok", "", "m", "stop"));
            },
        };

        var config = new AppConfig { MinRequestIntervalMs = 300, LockWaitSeconds = 5 }.Normalize();
        var service = new ChatCompletionService(temp.Pool, upstream, config);

        for (var i = 0; i < 3; i++) await service.CompleteAsync(Request(), CancellationToken.None);

        Assert.Equal(3, stamps.Count);

        // Two accounts and three requests: the third must still wait its turn
        // rather than stacking on the previous send.
        Assert.InRange(stamps[1] - stamps[0], 250, 2_000);
        Assert.InRange(stamps[2] - stamps[1], 250, 2_000);
    }

    [Fact]
    public async Task Pacing_Disabled_SendsBackToBack()
    {
        using var temp = new TempAccountPool(("a", 0), ("b", 0));
        var stamps = new List<long>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var upstream = new FakeUpstream
        {
            Handler = (account, _, _) =>
            {
                stamps.Add(clock.ElapsedMilliseconds);
                return Task.FromResult(new UpstreamResult("ok", "", "m", "stop"));
            },
        };

        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        for (var i = 0; i < 3; i++) await service.CompleteAsync(Request(), CancellationToken.None);

        Assert.Equal(3, stamps.Count);
        Assert.True(stamps[2] - stamps[0] < 250, "pacing must be off when the interval is 0");
    }

    [Fact]
    public async Task AHungUpstream_IsCutOffByTheRequestDeadline()
    {
        using var temp = new TempAccountPool(("a", 0));
        var upstream = new FakeUpstream
        {
            Handler = async (_, _, ct) =>
            {
                // The real client honours the token on every await, and so must this.
                await Task.Delay(Timeout.Infinite, ct);
                return new UpstreamResult("never", "", "m", "stop");
            },
        };

        var config = new AppConfig { RequestDeadlineSeconds = 5, UpstreamTimeoutSeconds = 5, LockWaitSeconds = 1 }
            .Normalize();
        var service = new ChatCompletionService(temp.Pool, upstream, config);

        // The client is not the one cancelling, so this must surface as a timeout.
        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => service.CompleteAsync(Request(), CancellationToken.None));

        Assert.Contains("deadline", ex.Message);
    }
}