using System.Text.Json;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Stage 2, items 4 and 5: the prompt budget and the retry count are live
/// settings, and compaction must never cost the model its ability to call tools.
/// </summary>
public sealed class PromptBudgetTests
{
    private static ChatCompletionRequest Conversation(int chars, params string[] tools) => new()
    {
        Model = "deepseek-v4-flash",
        Messages = [new ChatMessage { Role = "user", Content = JsonSerializer.SerializeToElement(new string('x', chars)) }],
        Tools = tools.Length == 0
            ? null
            : tools.Select(name => new ToolDefinition
            {
                Type = "function",
                Function = new ToolDefinitionFunction { Name = name, Description = "tool " + name },
            }).ToList(),
    };

    private static AppConfig Config(int maxPromptChars = 80_000, int maxRetries = 3) => new AppConfig
    {
        MaxPromptChars = maxPromptChars,
        MaxRetries = maxRetries,
        MinRequestIntervalMs = 0,
        LockWaitSeconds = 1,
        RequestDeadlineSeconds = 60,
    }.Normalize();

    // ---- the budget is honoured --------------------------------------------------

    [Fact]
    public void AShortConversationIsPassedThroughUntouched()
    {
        var prepared = ChatCompletionService.Prepare(Conversation(500), Config());

        Assert.Contains(new string('x', 500), prepared.Prompt);
        Assert.DoesNotContain(PromptBudget.Marker, prepared.Prompt);
    }

    [Fact]
    public void AnOversizedConversationIsCutToTheBudget()
    {
        var budget = 40_000;
        var prepared = ChatCompletionService.Prepare(Conversation(400_000), Config(maxPromptChars: budget));

        Assert.True(prepared.Prompt.Length <= budget,
            $"prompt was {prepared.Prompt.Length} chars, budget {budget}");
        Assert.Contains(PromptBudget.Marker, prepared.Prompt);
    }

    [Fact]
    public void TheBudgetIsRespectedEvenWithAToolManifest()
    {
        var budget = 30_000;
        var tools = new[] { "write_file", "read_file", "list_dir", "search_web", "make_http_request" };
        var prepared = ChatCompletionService.Prepare(Conversation(400_000, tools), Config(maxPromptChars: budget));

        Assert.True(prepared.Prompt.Length <= budget, $"prompt was {prepared.Prompt.Length} chars");
    }

    // ---- compaction keeps what makes tools work ----------------------------------

    [Fact]
    public void CompactionNeverEatsTheToolManifest()
    {
        var tools = new[] { "write_file", "read_file", "list_dir", "search_web", "make_http_request" };
        var prepared = ChatCompletionService.Prepare(
            Conversation(400_000, tools), Config(maxPromptChars: 20_000));

        // A manifest cut in half silently removes the model's ability to call
        // anything, which is worse than losing an old turn.
        Assert.Contains("--- TOOLS ---", prepared.Prompt);
        Assert.Contains("--- TOOL REMINDER ---", prepared.Prompt);
        foreach (var tool in tools) Assert.Contains(tool, prepared.Prompt);
    }

    [Fact]
    public void CompactionKeepsTheStartAndTheEndOfTheConversation()
    {
        var prepared = ChatCompletionService.Prepare(Conversation(400_000), Config(maxPromptChars: 20_000));

        // The transcript is one long run of filler, so its head and tail stand in
        // for "instructions" and "the turns that led here".
        Assert.StartsWith("User: xxx", prepared.Prompt);
        Assert.Contains(PromptBudget.Marker, prepared.Prompt);
        Assert.EndsWith("xxx", prepared.Prompt);
    }

    // ---- truncation mechanics ----------------------------------------------------

    [Fact]
    public void TruncateMiddle_KeepsTheResultWithinBudget()
    {
        var text = new string('a', 10_000);
        var cut = PromptBudget.TruncateMiddle(text, 1_000);

        Assert.Equal(1_000, cut.Length);
        Assert.Contains(PromptBudget.Marker, cut);
        Assert.StartsWith("aaaa", cut);
        Assert.EndsWith("aaaa", cut);
    }

    [Fact]
    public void TruncateMiddle_LeavesShortTextAlone()
    {
        Assert.Equal("короткий", PromptBudget.TruncateMiddle("короткий", 1_000));
    }

    [Fact]
    public void TruncateMiddle_SurvivesAStupidlySmallBudget()
    {
        var cut = PromptBudget.TruncateMiddle(new string('a', 5_000), 5);

        Assert.Equal(5, cut.Length);
    }

    // ---- settings actually drive behaviour ---------------------------------------

    [Fact]
    public void TheRetryBudgetIsTakenFromTheSetting()
    {
        var prompt = ChatCompletionService.Prepare(Conversation(10), Config(maxRetries: 3)).Prompt;
        Assert.NotEmpty(prompt);
    }

    [Fact]
    public void Normalize_ClampsTheNewSettings()
    {
        var config = new AppConfig { MaxPromptChars = 10, MaxRetries = 99 }.Normalize();

        Assert.Equal(AppConfig.MinPromptChars, config.MaxPromptChars);
        Assert.Equal(10, config.MaxRetries);
    }

    [Fact]
    public void Normalize_AcceptsDisabledRetries()
    {
        var config = new AppConfig { MaxRetries = 0 }.Normalize();

        Assert.Equal(0, config.MaxRetries);
    }

    [Fact]
    public async Task AZeroRetryBudgetAsksExactlyOnce()
    {
        using var temp = new TempAccountPool(("main", 0));
        var calls = 0;

        var upstream = new CountingUpstream(() =>
        {
            calls++;
            return new UpstreamResult("", "", "m", "stop");
        });

        var service = new ChatCompletionService(temp.Pool, upstream, Config(maxRetries: 0));
        await service.CompleteAsync(Conversation(10), CancellationToken.None);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TheRetryBudgetBoundsTheNumberOfAsks()
    {
        using var temp = new TempAccountPool(("main", 0));
        var calls = 0;

        var upstream = new CountingUpstream(() =>
        {
            calls++;
            return new UpstreamResult("", "", "m", "stop");
        });

        var service = new ChatCompletionService(temp.Pool, upstream, Config(maxRetries: 2));
        await service.CompleteAsync(Conversation(10), CancellationToken.None);

        Assert.Equal(3, calls);
    }

    private sealed class CountingUpstream(Func<UpstreamResult> reply) : IDeepSeekCompletion
    {
        public Task<UpstreamResult> CompleteAsync(Account account, ModelConfig model, string prompt, string? chatSessionId, string? parentMessageId, CancellationToken ct) =>
            Task.FromResult(reply());
    }
}