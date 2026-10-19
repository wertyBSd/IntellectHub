using System.Text.Json;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// One remote DeepSeek chat per conversation, and nothing shared between them.
///
/// Two ways a conversation can end up talking to the wrong chat:
/// a second client reusing an existing session key, and an internal round — a
/// continuation of a cut-off answer — overwriting the session the next turn will
/// resume from. The second is not obvious and produced genuinely mixed context.
/// </summary>
public sealed class SessionIsolationTests
{
    /// <summary>Records which remote chat each hop was sent to.</summary>
    private sealed class RecordingUpstream : IDeepSeekCompletion
    {
        public List<string?> Sessions { get; } = [];
        public List<string?> Parents { get; } = [];
        public List<string> Prompts { get; } = [];
        public List<string> Accounts { get; } = [];

        /// <summary>Reply to give on each successive call.</summary>
        public Queue<string> Replies { get; } = new();

        /// <summary>Queues the scripted replies for one run.</summary>
        public RecordingUpstream Script(params string[] replies)
        {
            foreach (var reply in replies) Replies.Enqueue(reply);
            return this;
        }

        public Task<UpstreamResult> CompleteAsync(Account account, ModelConfig model, string prompt,
            string? chatSessionId, string? parentMessageId, CancellationToken ct)
        {
            Sessions.Add(chatSessionId);
            Parents.Add(parentMessageId);
            Prompts.Add(prompt);
            Accounts.Add(account.Id);

            var content = Replies.Count > 0 ? Replies.Dequeue() : "ok";
            var messageId = $"msg-{Sessions.Count}";

            // Mirrors DeepSeek: continuing a chat yields the same session id, opening
            // a new one yields a fresh id.
            var session = chatSessionId ?? $"session-{Sessions.Count}";

            return Task.FromResult(new UpstreamResult(content, "", messageId, "stop") { ChatSessionId = session });
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

    // ---- one chat per client --------------------------------------------------

    [Fact]
    public async Task TwoClientsNeverShareARemoteChat()
    {
        using var temp = new TempAccountPool(("main", 0));
        var upstream = new RecordingUpstream();
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        await service.CompleteAsync(Turn("client-a", Msg("user", "а")), CancellationToken.None);
        await service.CompleteAsync(Turn("client-b", Msg("user", "б")), CancellationToken.None);

        // Each client's first turn has to open its own chat.
        Assert.Null(upstream.Sessions[0]);
        Assert.Null(upstream.Sessions[1]);

        // ...and neither may see the other's conversation.
        Assert.DoesNotContain("б", upstream.Prompts[0]);
        Assert.DoesNotContain("а", upstream.Prompts[1]);
    }

    [Fact]
    public async Task OneClientKeepsChattingAfterAnotherClientArrives()
    {
        using var temp = new TempAccountPool(("main", 0));
        var upstream = new RecordingUpstream();
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        await service.CompleteAsync(Turn("a", Msg("user", "первый")), CancellationToken.None);
        await service.CompleteAsync(Turn("b", Msg("user", "чужой")), CancellationToken.None);
        await service.CompleteAsync(Turn("a",
            Msg("user", "первый"), Msg("assistant", "ответ"), Msg("user", "второй")), CancellationToken.None);

        // The interleaved client must not have cost the first one its session.
        Assert.NotNull(upstream.Sessions[2]);
        Assert.DoesNotContain("чужой", upstream.Prompts[2]);
        Assert.Contains("второй", upstream.Prompts[2]);
    }

    /// <summary>
    /// A client that restarts keeps the same session key in some setups but starts
    /// a new transcript. The gateway must refuse to attach the old remote chat to
    /// that history rather than mixing the two conversations.
    /// </summary>
    [Fact]
    public async Task AKeyReusedWithADifferentTranscriptIsNotAttachedToTheOldChat()
    {
        using var temp = new TempAccountPool(("main", 0));
        var upstream = new RecordingUpstream();
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        await service.CompleteAsync(Turn("same-key", Msg("user", "разговор один")), CancellationToken.None);

        await service.CompleteAsync(Turn("same-key",
            Msg("user", "совсем другой разговор"),
            Msg("assistant", "ответ"),
            Msg("user", "ещё")), CancellationToken.None);

        Assert.Null(upstream.Sessions[1]);
        Assert.DoesNotContain("разговор один", upstream.Prompts[1]);
        Assert.Contains("совсем другой разговор", upstream.Prompts[1]);
    }

    // ---- internal rounds must not steal the conversation's chat ---------------

    /// <summary>
    /// The regression this covers: a cut-off answer is finished by extra round trips
    /// on throwaway chats. Each of those used to overwrite the recorded session, so
    /// the next turn resumed a chat that had been assembled to continue a fragment
    /// rather than to hold the conversation.
    /// </summary>
    [Fact]
    public async Task ATruncatedAnswerDoesNotLeaveTheConversationTalkingToAContinuationChat()
    {
        using var temp = new TempAccountPool(("main", 0));
        // Long enough to trip the cut-off heuristic, so continuations actually run.
        var upstream = new RecordingUpstream().Script(new string('x', 26_000), "…и конец", "…и конец");
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        await service.CompleteAsync(Turn("a", Msg("user", "длинный вопрос")), CancellationToken.None);

        var chatAfterContinuation = upstream.Sessions.Count;
        Assert.True(chatAfterContinuation > 1, "expected at least one continuation round trip");

        // The next turn of the same conversation must not resume a continuation chat.
        await service.CompleteAsync(Turn("a",
            Msg("user", "длинный вопрос"),
            Msg("assistant", "ответ"),
            Msg("user", "а теперь другое")), CancellationToken.None);

        var lastPrompt = upstream.Prompts[^1];
        Assert.DoesNotContain("…и конец", lastPrompt);
        Assert.Contains("а теперь другое", lastPrompt);
    }

    [Fact]
    public async Task TheNextTurnContinuesTheChatTheAnswerActuallyCameFrom()
    {
        using var temp = new TempAccountPool(("main", 0));
        var upstream = new RecordingUpstream().Script(new string('x', 26_000), "хвост");
        var service = new ChatCompletionService(temp.Pool, upstream, Config());

        await service.CompleteAsync(Turn("a", Msg("user", "длинный вопрос")), CancellationToken.None);
        await service.CompleteAsync(Turn("a",
            Msg("user", "длинный вопрос"),
            Msg("assistant", "ответ"),
            Msg("user", "дальше")), CancellationToken.None);

        // Sessions[0] opened the conversation; the next turn must resume exactly it,
        // not one of the continuation chats opened afterwards.
        Assert.Equal(upstream.Sessions[0] ?? "session-1", upstream.Sessions[^1]);
    }
}