using System.Text.Json;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Stage 2: a cut-off answer, an empty answer and an oversized prompt must heal
/// themselves instead of reaching the client as a broken result.
/// </summary>
public sealed class RecoveryTests
{
    private sealed class ScriptedUpstream : IDeepSeekCompletion
    {
        private readonly Queue<Func<UpstreamResult>> _turns = new();
        private Func<UpstreamResult>? _last;
        public List<string> Prompts { get; } = [];
        public List<string> Accounts { get; } = [];

        public ScriptedUpstream(params Func<UpstreamResult>[] turns)
        {
            foreach (var turn in turns) _turns.Enqueue(turn);
            _last = turns.Length > 0 ? turns[^1] : null;
        }

        public Task<UpstreamResult> CompleteAsync(Account account, ModelConfig model, string prompt, string? chatSessionId, string? parentMessageId, CancellationToken ct)
        {
            Prompts.Add(prompt);
            Accounts.Add(account.Id);

            // Once the script runs out the last turn repeats, so a test that wants
            // "this never works" does not silently start succeeding.
            var turn = _turns.Count > 0 ? _turns.Dequeue() : _last!;

            // The method is async on the interface's behalf: a scripted throw must
            // surface as a faulted task, exactly like a real upstream error.
            return Task.FromResult(turn());
        }
    }

    private static AppConfig Config() => new AppConfig
    {
        MinRequestIntervalMs = 0,
        LockWaitSeconds = 2,
        RequestDeadlineSeconds = 60,
    }.Normalize();

    private static ChatCompletionRequest LongRequest(int chars)
    {
        var filler = new string('x', chars);
        return new ChatCompletionRequest
        {
            Model = "deepseek-v4-flash",
            Messages = [new ChatMessage { Role = "user", Content = JsonSerializer.SerializeToElement(filler) }],
        };
    }

    // ---- continuation ----------------------------------------------------------

    [Fact]
    public async Task ACutOffAnswer_IsCompletedWithAContinuationRound()
    {
        using var temp = new TempAccountPool(("main", 0));

        var upstream = new ScriptedUpstream(
            () => new UpstreamResult("Первая часть ответа", "", "m1", "length"),
            () => new UpstreamResult("Вторая часть ответа", "", "m2", "stop"));

        var service = new ChatCompletionService(temp.Pool, upstream, Config());
        var result = await service.CompleteAsync(LongRequest(10), CancellationToken.None);

        Assert.Contains("Первая часть ответа", result.Content);
        Assert.Contains("Вторая часть ответа", result.Content);
        Assert.Equal(2, upstream.Prompts.Count);
    }

    [Fact]
    public async Task TheContinuationPrompt_QuotesThePartialAnswerBack()
    {
        using var temp = new TempAccountPool(("main", 0));

        var upstream = new ScriptedUpstream(
            () => new UpstreamResult("Мы начали писать игру про трактор", "", "m1", "length"),
            () => new UpstreamResult("...и закончили её", "", "m2", "stop"));

        var service = new ChatCompletionService(temp.Pool, upstream, Config());
        await service.CompleteAsync(LongRequest(10), CancellationToken.None);

        // The gateway re-sends the whole transcript on a fresh session each turn,
        // so the continuation cannot rely on DeepSeek remembering anything.
        Assert.StartsWith("[CONTINUATION]", upstream.Prompts[1]);
        Assert.Contains("Мы начали писать игру про трактор", upstream.Prompts[1]);
        Assert.Contains("Do not repeat", upstream.Prompts[1]);
    }

    [Fact]
    public async Task AVeryLongAnswerIsTreatedAsCutOffEvenWithoutTheLengthReason()
    {
        using var temp = new TempAccountPool(("main", 0));

        var upstream = new ScriptedUpstream(
            () => new UpstreamResult(new string('a', 26_000), "", "m1", "stop"),
            () => new UpstreamResult(" хвост", "", "m2", "stop"));

        var service = new ChatCompletionService(temp.Pool, upstream, Config());
        var result = await service.CompleteAsync(LongRequest(10), CancellationToken.None);

        Assert.Equal(2, upstream.Prompts.Count);
        Assert.Contains(" хвост", result.Content);
    }

    [Fact]
    public async Task AShortAnswerIsNotContinued()
    {
        using var temp = new TempAccountPool(("main", 0));

        var upstream = new ScriptedUpstream(() => new UpstreamResult("Короткий ответ", "", "m1", "stop"));
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        var result = await service.CompleteAsync(LongRequest(10), CancellationToken.None);

        Assert.Single(upstream.Prompts);
        Assert.Equal("Короткий ответ", result.Content);
    }

    [Fact]
    public async Task ContinuationIsCappedAtTwoRounds()
    {
        using var temp = new TempAccountPool(("main", 0));

        // Every turn reports finish=length, so the loop keeps wanting another.
        var upstream = new ScriptedUpstream(
            () => new UpstreamResult("часть1", "", "m1", "length"),
            () => new UpstreamResult("часть2", "", "m2", "length"),
            () => new UpstreamResult("часть3", "", "m3", "length"),
            () => new UpstreamResult("часть4", "", "m4", "stop"));

        var service = new ChatCompletionService(temp.Pool, upstream, Config());
        var result = await service.CompleteAsync(LongRequest(10), CancellationToken.None);

        // One ask plus at most two continuations, then stop and hand over
        // whatever arrived instead of asking forever.
        Assert.Equal(3, upstream.Prompts.Count);
        Assert.Contains("часть3", result.Content);
        Assert.DoesNotContain("часть4", result.Content);
    }

    [Fact]
    public async Task ACompletedContinuationIsNotContinuedJustForBeingLong()
    {
        using var temp = new TempAccountPool(("main", 0));

        // The model answered fully but verbosely. Two continuations' worth of text
        // must not be mistaken for "cut off" on the combined result.
        var upstream = new ScriptedUpstream(
            () => new UpstreamResult(new string('a', 26_000), "", "m1", "stop"),
            () => new UpstreamResult(new string('b', 26_000), "", "m2", "stop"),
            () => new UpstreamResult("лишнее", "", "m3", "stop"));

        var service = new ChatCompletionService(temp.Pool, upstream, Config());
        await service.CompleteAsync(LongRequest(10), CancellationToken.None);

        Assert.Equal(2, upstream.Prompts.Count);
    }

    [Fact]
    public async Task AThrottledContinuation_ReturnsThePartialAnswer()
    {
        using var temp = new TempAccountPool(("main", 0), ("spare", 0));
        using var capture = new LogCapture();

        var upstream = new ScriptedUpstream(
            () => new UpstreamResult("Первая часть", "", "m1", "length"),
            () => throw new UpstreamRateLimitException("Слишком частые сообщения"));

        var service = new ChatCompletionService(temp.Pool, upstream, Config());
        var result = await service.CompleteAsync(LongRequest(10), CancellationToken.None);

        // Failing the whole request would throw away an answer the model already produced.
        Assert.Equal("Первая часть", result.Content);
        Assert.Contains("продолжение отменено троттлингом", capture.Joined);
    }

    // ---- empty reply -----------------------------------------------------------

    [Fact]
    public async Task AnEmptyReply_IsRetriedWithASmallerPrompt()
    {
        using var temp = new TempAccountPool(("main", 0));
        using var capture = new LogCapture();

        var upstream = new ScriptedUpstream(
            () => new UpstreamResult("", "", "m1", "stop"),
            () => new UpstreamResult("Наконец-то ответ", "", "m2", "stop"));

        var service = new ChatCompletionService(temp.Pool, upstream, Config());
        var result = await service.CompleteAsync(LongRequest(100), CancellationToken.None);

        Assert.Equal("Наконец-то ответ", result.Content);
        Assert.Equal(2, upstream.Prompts.Count);
        Assert.Contains("пустой ответ", capture.Joined);
        Assert.Contains("восстановились с попытки", capture.Joined);
    }

    [Fact]
    public async Task AReplyWithOnlyReasoningCountsAsAnswered()
    {
        using var temp = new TempAccountPool(("main", 0));

        var upstream = new ScriptedUpstream(() => new UpstreamResult("", "думаю...", "m1", "stop"));
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        await service.CompleteAsync(LongRequest(10), CancellationToken.None);

        Assert.Single(upstream.Prompts);
    }

    [Fact]
    public async Task PersistentEmptinessGivesUpInsteadOfLoopingForever()
    {
        using var temp = new TempAccountPool(("main", 0));
        using var capture = new LogCapture();

        var upstream = new ScriptedUpstream(() => new UpstreamResult("", "", "m", "stop"));
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        await service.CompleteAsync(LongRequest(10), CancellationToken.None);

        // One ask plus three retries, then give up and report it.
        Assert.Equal(4, upstream.Prompts.Count);
        Assert.Contains("раз подряд ответил пустотой", capture.Joined);
    }

    // ---- oversized prompt ------------------------------------------------------

    [Fact]
    public async Task AnOversizedPrompt_IsRetriedWithAShrunkOne()
    {
        using var temp = new TempAccountPool(("main", 0));
        using var capture = new LogCapture();

        var upstream = new ScriptedUpstream(
            () => throw new UpstreamException("context too long", 400, errorKind: ErrorKinds.ContextTooLong),
            () => new UpstreamResult("Ответ после усечения", "", "m2", "stop"));

        var service = new ChatCompletionService(temp.Pool, upstream, Config());
        var result = await service.CompleteAsync(LongRequest(200_000), CancellationToken.None);

        Assert.Equal("Ответ после усечения", result.Content);
        Assert.Equal(2, upstream.Prompts.Count);
        Assert.Contains("[Earlier context compacted by IntellectHub]", upstream.Prompts[1]);
        Assert.Contains("контекст не помещается", capture.Joined);
    }

    [Fact]
    public async Task ARepeatedlyOversizedPromptEventuallySurfacesTheError()
    {
        using var temp = new TempAccountPool(("main", 0));

        var upstream = new ScriptedUpstream(
            () => throw new UpstreamException("context too long", 400, errorKind: ErrorKinds.ContextTooLong));

        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => service.CompleteAsync(LongRequest(200_000), CancellationToken.None));

        Assert.Equal(ErrorKinds.ContextTooLong, ex.ErrorKind);
        Assert.Equal(4, upstream.Prompts.Count);
    }

    [Fact]
    public async Task ShrinkingGetsAggressiveForAnOversizedPrompt()
    {
        using var temp = new TempAccountPool(("main", 0));

        var upstream = new ScriptedUpstream(
            () => throw new UpstreamException("context too long", 400, errorKind: ErrorKinds.ContextTooLong),
            () => throw new UpstreamException("context too long", 400, errorKind: ErrorKinds.ContextTooLong),
            () => new UpstreamResult("ok", "", "m", "stop"));

        var service = new ChatCompletionService(temp.Pool, upstream, Config());
        await service.CompleteAsync(LongRequest(400_000), CancellationToken.None);

        Assert.Equal(3, upstream.Prompts.Count);
        Assert.True(upstream.Prompts[1].Length < upstream.Prompts[0].Length,
            "the first retry must actually be shorter");
        Assert.True(upstream.Prompts[2].Length < upstream.Prompts[1].Length,
            "an oversized prompt needs a deeper cut on the next retry");
    }
}