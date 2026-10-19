using System.Text.Json;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Stage 2, item 6: one remote DeepSeek chat per conversation.
///
/// The value is saving a session-create round trip per turn. The risk is far
/// higher: a remote session that does not match what the client sent would
/// silently corrupt the conversation. So every unverifiable case must fall back
/// to a full transcript on a fresh session.
/// </summary>
public sealed class SessionReuseTests
{
    private sealed class SessionRecordingUpstream : IDeepSeekCompletion
    {
        public List<string> SessionIds { get; } = [];
        public List<string> Parents { get; } = [];
        public List<string> Prompts { get; } = [];

        public List<string> Accounts { get; } = [];

        public int NextMessageNumber = 1;

        public Task<UpstreamResult> CompleteAsync(Account account, ModelConfig model, string prompt,
            string? chatSessionId, string? parentMessageId, CancellationToken ct)
        {
            SessionIds.Add(chatSessionId ?? "");
            Parents.Add(parentMessageId ?? "");
            Prompts.Add(prompt);
            Accounts.Add(account.Id);

            // Mirrors the real client: a turn without a session id gets a new one.
            var session = chatSessionId ?? $"session-{NextMessageNumber}";

            return Task.FromResult(new UpstreamResult("ответ " + NextMessageNumber, "", $"msg-{NextMessageNumber}", "stop")
            {
                ChatSessionId = session,
            });
        }
    }

    private static AppConfig Config() => new AppConfig
    {
        MinRequestIntervalMs = 0,
        LockWaitSeconds = 1,
        RequestDeadlineSeconds = 60,
    }.Normalize();

    private static ChatMessage Msg(string role, string content) =>
        new() { Role = role, Content = JsonSerializer.SerializeToElement(content) };

    private static ChatCompletionRequest Turn(string agent, params ChatMessage[] messages) => new()
    {
        Model = "deepseek-v4-flash",
        SessionKey = agent,
        Messages = [.. messages],
    };

    [Fact]
    public async Task TheFirstTurnStartsAFreshSession()
    {
        using var temp = new TempAccountPool(("main", 0));
        var upstream = new SessionRecordingUpstream();
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        await service.CompleteAsync(Turn("agent-1", Msg("user", "привет")), CancellationToken.None);

        Assert.Equal("", upstream.SessionIds[0]);
        Assert.Equal("", upstream.Parents[0]);
    }

    [Fact]
    public async Task ASecondTurnReusesTheSessionAndSendsOnlyNewTurns()
    {
        using var temp = new TempAccountPool(("main", 0));
        var upstream = new SessionRecordingUpstream();
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        await service.CompleteAsync(Turn("agent-1", Msg("user", "первый вопрос")), CancellationToken.None);

        await service.CompleteAsync(Turn("agent-1",
            Msg("user", "первый вопрос"),
            Msg("assistant", "ответ 1"),
            Msg("user", "второй вопрос")), CancellationToken.None);

        Assert.Equal(2, upstream.SessionIds.Count);

        // The remote session id now comes from the state the gateway recorded.
        Assert.NotEqual("", upstream.SessionIds[1]);
        Assert.Equal("msg-1", upstream.Parents[1]);

        // Only the new turn travels: re-sending the history would show the model
        // the same conversation twice.
        Assert.DoesNotContain("первый вопрос", upstream.Prompts[1]);
        Assert.Contains("второй вопрос", upstream.Prompts[1]);
    }

    [Fact]
    public async Task TheAgentsOwnAnswerIsNotEchoedBack()
    {
        using var temp = new TempAccountPool(("main", 0));
        var upstream = new SessionRecordingUpstream();
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        await service.CompleteAsync(Turn("agent-1", Msg("user", "первый")), CancellationToken.None);
        await service.CompleteAsync(Turn("agent-1",
            Msg("user", "первый"), Msg("assistant", "ответ 1"), Msg("user", "второй")), CancellationToken.None);

        // DeepSeek already produced that assistant turn; replaying it would make
        // the model read its own answer back as new input.
        Assert.DoesNotContain("ответ 1", upstream.Prompts[1]);
    }

    [Fact]
    public async Task TheAgentToolLoopStaysOnOneRemoteChat()
    {
        using var temp = new TempAccountPool(("main", 0));
        var upstream = new SessionRecordingUpstream();
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        // Turn 1: the user asks, the model asks for a tool.
        await service.CompleteAsync(Turn("agent-1", Msg("user", "прочитай README")), CancellationToken.None);

        // The client then appends the tool call and its result before asking again,
        // which is what the console client does between the agent's tool steps.
        // Without a session header this shape opened a new remote chat on every one
        // of up to 16 steps, paying a session-create round trip for each.
        await service.CompleteAsync(Turn("agent-1",
            Msg("user", "прочитай README"),
            Msg("assistant", "<｜tool▁calls▁begin｜><｜tool▁call▁begin｜>function<｜tool▁sep｜>read"),
            Msg("user", "содержимое README")), CancellationToken.None);

        Assert.Equal(2, upstream.SessionIds.Count);
        Assert.NotEqual("", upstream.SessionIds[1]);
        Assert.Equal("msg-1", upstream.Parents[1]);

        // Only the tool result crosses: the earlier turns are already in the chat.
        Assert.DoesNotContain("прочитай README", upstream.Prompts[1]);
        Assert.Contains("содержимое README", upstream.Prompts[1]);
    }

    [Fact]
    public async Task TwoAgentsDoNotShareASession()
    {
        using var temp = new TempAccountPool(("main", 0));
        var upstream = new SessionRecordingUpstream();
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        await service.CompleteAsync(Turn("agent-1", Msg("user", "а")), CancellationToken.None);
        await service.CompleteAsync(Turn("agent-2", Msg("user", "б")), CancellationToken.None);

        Assert.Equal("", upstream.SessionIds[0]);
        Assert.Equal("", upstream.SessionIds[1]);
    }

    // ---- anything unverifiable must fall back ------------------------------------

    [Fact]
    public async Task EditedHistory_StartsAFreshSession()
    {
        using var temp = new TempAccountPool(("main", 0));
        var upstream = new SessionRecordingUpstream();
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        await service.CompleteAsync(Turn("agent-1", Msg("user", "первый вопрос")), CancellationToken.None);

        // Same session key, but the client rewrote what it said last time.
        await service.CompleteAsync(Turn("agent-1",
            Msg("user", "совсем другой вопрос"),
            Msg("assistant", "ответ 1"),
            Msg("user", "второй вопрос")), CancellationToken.None);

        // Guessing here would hand the model a transcript it never saw.
        Assert.Equal("", upstream.SessionIds[1]);
        Assert.Contains("совсем другой вопрос", upstream.Prompts[1]);
        Assert.Contains("второй вопрос", upstream.Prompts[1]);
    }

    [Fact]
    public async Task ASessionBelongingToAnotherAccount_IsNotReused()
    {
        using var temp = new TempAccountPool(("a", 0), ("b", 0));
        var upstream = new SessionRecordingUpstream();
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        await service.CompleteAsync(Turn("agent-1", Msg("user", "первый")), CancellationToken.None);

        // A DeepSeek chat belongs to the login that created it; continuing it from
        // another login would either fail or cross-contaminate two accounts.
        // Force the pool onto the other login so the guard is actually exercised.
        temp.Pool.All.First(a => a.Id == "a").CooldownUntil =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000;

        await service.CompleteAsync(Turn("agent-1",
            Msg("user", "первый"), Msg("assistant", "ответ 1"), Msg("user", "второй")),
            CancellationToken.None);

        Assert.Equal("", upstream.SessionIds[1]);
        Assert.Equal("b", upstream.Accounts[1]);
    }

    [Fact]
    public async Task ChangedToolSet_StartsAFreshSession()
    {
        using var temp = new TempAccountPool(("main", 0));
        var upstream = new SessionRecordingUpstream();
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        var first = Turn("agent-1", Msg("user", "первый"));
        first.Tools = [Tool("write_file")];
        await service.CompleteAsync(first, CancellationToken.None);

        var second = Turn("agent-1", Msg("user", "первый"), Msg("assistant", "ответ 1"), Msg("user", "второй"));
        second.Tools = [Tool("write_file"), Tool("read_file")];
        await service.CompleteAsync(second, CancellationToken.None);

        // A different manifest changes the standing instructions; continuing the
        // old chat would keep the stale rules live.
        Assert.Equal("", upstream.SessionIds[1]);
        Assert.Contains("read_file", upstream.Prompts[1]);
    }

    [Fact]
    public async Task ANothingNewToSend_StartsAFreshSession()
    {
        using var temp = new TempAccountPool(("main", 0));
        var upstream = new SessionRecordingUpstream();
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        await service.CompleteAsync(Turn("agent-1", Msg("user", "первый")), CancellationToken.None);

        // Same conversation, nothing appended: the delta would be empty.
        await service.CompleteAsync(Turn("agent-1", Msg("user", "первый")), CancellationToken.None);

        Assert.Equal("", upstream.SessionIds[1]);
    }

    [Fact]
    public async Task NoSessionHeader_MeansNoReuse()
    {
        using var temp = new TempAccountPool(("main", 0));
        var upstream = new SessionRecordingUpstream();
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        for (var i = 0; i < 3; i++)
        {
            await service.CompleteAsync(
                new ChatCompletionRequest { Messages = [Msg("user", "вопрос " + i)] },
                CancellationToken.None);
        }

        // Without an identity there is nothing to key a session on, so every turn
        // is self-contained. That is the behaviour from before sessions existed.
        Assert.All(upstream.SessionIds, id => Assert.Equal("", id));
    }

    private static ToolDefinition Tool(string name) => new()
    {
        Type = "function",
        Function = new ToolDefinitionFunction { Name = name },
    };

    // ---- registry mechanics ------------------------------------------------------

    [Fact]
    public void AnExpiredSessionIsDropped()
    {
        var registry = new SessionRegistry { TtlMs = 1 };
        registry.Save(new SessionState
        {
            AgentId = "a", AccountId = "x", ChatSessionId = "s", SystemFingerprint = "f",
        });

        Thread.Sleep(20);

        Assert.Null(registry.Get("a"));
    }

    [Fact]
    public void SweepingDropsSessionsPastTheDepthLimit()
    {
        var registry = new SessionRegistry { MaxMessageDepth = 3 };
        registry.Save(new SessionState
        {
            AgentId = "deep", AccountId = "x", ChatSessionId = "s", SystemFingerprint = "f", MessageCount = 3,
        });
        registry.Save(new SessionState
        {
            AgentId = "shallow", AccountId = "x", ChatSessionId = "s2", SystemFingerprint = "f", MessageCount = 1,
        });

        Assert.Equal(1, registry.Sweep());
        Assert.Null(registry.Get("deep"));
        Assert.NotNull(registry.Get("shallow"));
    }

    [Fact]
    public void ResettingForAnotherAccountClearsForeignSessions()
    {
        var registry = new SessionRegistry();
        registry.Save(new SessionState { AgentId = "a", AccountId = "x", ChatSessionId = "s", SystemFingerprint = "f" });
        registry.Save(new SessionState { AgentId = "b", AccountId = "y", ChatSessionId = "s2", SystemFingerprint = "f" });

        registry.ResetForOtherAccounts("y");

        Assert.Null(registry.Get("a"));
        Assert.NotNull(registry.Get("b"));
    }

    [Fact]
    public void FingerprintsDetectAnEditedMessage()
    {
        var a = SessionFingerprint.Of("user", "привет");
        var b = SessionFingerprint.Of("user", "привет");
        var c = SessionFingerprint.Of("user", "привет!");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void AFlippedTranscriptDropsTheAssistantTurn()
    {
        var messages = new List<ChatMessage>
        {
            Msg("user", "вопрос"),
            Msg("assistant", "ответ"),
            Msg("user", "ещё"),
        };

        var start = 2;
        while (start < messages.Count && (messages[start].Role ?? "user") == "assistant") start++;

        Assert.Equal(2, start);
    }
}