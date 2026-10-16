using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Captures the chat log during a full request. The console is the main diagnostic
/// surface, so the wording and the volume are part of the contract, not decoration.
/// </summary>
public sealed class LogTraceTests
{
    private sealed class FakeUpstream : IDeepSeekCompletion
    {
        public Func<Account, Task<UpstreamResult>>? Handler { get; set; }

        public Task<UpstreamResult> CompleteAsync(Account account, ModelConfig model, string prompt, string? chatSessionId, string? parentMessageId, CancellationToken ct) =>
            Handler?.Invoke(account) ?? Task.FromResult(new UpstreamResult("ok", "", "m", "stop"));
    }

    private static ChatCompletionRequest Request(string text = "напиши игру") => new()
    {
        Model = "deepseek-v4-flash",
        Messages = [new ChatMessage { Role = "user", Content = JsonSerializer.SerializeToElement(text) }],
    };

    private static AppConfig Config() => new AppConfig
    {
        MinRequestIntervalMs = 0,
        LockWaitSeconds = 2,
        MaxConcurrent = 24,
    }.Normalize();

    [Fact]
    public async Task ASuccessfulRequest_TellsTheWholeStory()
    {
        using var temp = new TempAccountPool(("main", 0));
        using var capture = new LogCapture();

        var upstream = new FakeUpstream
        {
            Handler = account => Task.FromResult(new UpstreamResult("Вот твоя игра", "", "m1", "stop")),
        };

        var service = new ChatCompletionService(temp.Pool, upstream, Config());
        await service.CompleteAsync(Request(), CancellationToken.None);

        var log = capture.Joined;

        Assert.Contains("запрос", log);
        Assert.Contains("модель=deepseek-v4-flash", log);
        Assert.Contains("аккаунт main", log);
        Assert.Contains("отправка в DeepSeek", log);
        Assert.Contains("ответ", log);
        Assert.Contains("finish=stop", log);
        Assert.Contains("итог", log);

        // The request tag must appear on every line of the request so concurrent
        // traffic stays readable.
        Assert.All(capture.Lines, line => Assert.StartsWith("#", line.Trim()));
    }

    [Fact]
    public async Task AToolCall_IsNamedInTheLog()
    {
        using var temp = new TempAccountPool(("main", 0));
        using var capture = new LogCapture();

        const string reply = "<｜DSML｜tool_calls><｜DSML｜invoke name=\"write_file\">" +
                             "<｜DSML｜parameter name=\"path\" string=\"true\">index.html</｜DSML｜parameter>" +
                             "</｜DSML｜invoke></｜DSML｜tool_calls>";

        var upstream = new FakeUpstream
        {
            Handler = _ => Task.FromResult(new UpstreamResult(reply, "", "m2", "stop")),
        };

        var request = Request();
        request.Tools =
        [
            new ToolDefinition
            {
                Type = "function",
                Function = new ToolDefinitionFunction { Name = "write_file" },
            },
        ];

        var service = new ChatCompletionService(temp.Pool, upstream, Config());
        await service.CompleteAsync(request, CancellationToken.None);

        Assert.Contains("инструмент write_file", capture.Joined);
        Assert.Contains("вызов инструмента: write_file", capture.Joined);
    }

    [Fact]
    public async Task AThrottledAccount_SaysWhichAccountAndForHowLong()
    {
        using var temp = new TempAccountPool(("main", 0), ("spare", 0));
        using var capture = new LogCapture();

        var upstream = new FakeUpstream
        {
            Handler = account => account.Id == "main"
                ? throw new UpstreamRateLimitException("Слишком частые сообщения")
                : Task.FromResult(new UpstreamResult("готово", "", "m3", "stop")),
        };

        var service = new ChatCompletionService(temp.Pool, upstream, Config());
        await service.CompleteAsync(Request(), CancellationToken.None);

        var log = capture.Joined;

        Assert.Contains("троттлинг", log);
        Assert.Contains("аккаунт main на паузе", log);
        Assert.Contains("аккаунт spare", log);
    }

    [Fact]
    public async Task ANativeToolCall_IsExplainedRatherThanPassedThrough()
    {
        using var temp = new TempAccountPool(("main", 0));
        using var capture = new LogCapture();

        const string reply = "<｜｜DSML｜｜ calls><｜｜DSML｜｜ invoke name=\"run_commands\">" +
                             "<｜｜DSML｜｜ parameter name=\"command\" string=\"true\">dir</｜｜DSML｜｜ parameter>" +
                             "</｜｜DSML｜｜ invoke></｜｜DSML｜｜ calls>";

        var calls = 0;
        var upstream = new FakeUpstream
        {
            // The first turn asks for a tool the gateway cannot run; the repair
            // round must come back with plain prose or the request fails by design.
            Handler = _ => Task.FromResult(
                Interlocked.Increment(ref calls) == 1
                    ? new UpstreamResult(reply, "", "m4", "stop")
                    : new UpstreamResult("Вот твоя игра", "", "m5", "stop")),
        };

        var service = new ChatCompletionService(temp.Pool, upstream, Config());
        await service.CompleteAsync(Request(), CancellationToken.None);

        var log = capture.Joined;

        Assert.Contains("встроенный инструмент DeepSeek: run_commands", log);
        Assert.Contains("повтор со строгим промптом", log);
    }

    [Fact]
    public async Task AFailureIsReportedWithItsKindAndTiming()
    {
        using var temp = new TempAccountPool(("main", 0));
        using var capture = new LogCapture();

        var upstream = new FakeUpstream
        {
            Handler = _ => throw new UpstreamException("upstream exploded", 500),
        };

        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        await Assert.ThrowsAsync<UpstreamException>(() => service.CompleteAsync(Request(), CancellationToken.None));

        var log = capture.Joined;
        Assert.Contains("ошибка 500", log);
        Assert.Contains("upstream exploded", log);
    }

    [Fact]
    public async Task PoolReloads_StaySilentUnlessSomethingChanged()
    {
        using var temp = new TempAccountPool(("main", 0));
        using var capture = new LogCapture();

        var upstream = new FakeUpstream
        {
            Handler = _ => Task.FromResult(new UpstreamResult("ok", "", "m", "stop")),
        };

        var service = new ChatCompletionService(temp.Pool, upstream, Config());
        await service.CompleteAsync(Request(), CancellationToken.None);
        await service.CompleteAsync(Request(), CancellationToken.None);

        // Reading the same unchanged files on every request must not spam the log.
        Assert.DoesNotContain("аккаунты перечитаны", capture.Joined);
    }
}