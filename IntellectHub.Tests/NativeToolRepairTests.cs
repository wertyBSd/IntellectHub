using System.Collections.Immutable;
using System.Text.Json;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// What happens when the model reaches for a tool the gateway cannot run.
///
/// DeepSeek Web has built-in tools the model is used to having. It has no channel
/// for them here, so a call to one is unreachable — but it is not "malformed",
/// and treating it as such wastes the repair round on a prompt that never mentions
/// the actual problem.
///
/// The case that exposed this: asked to write a long document, the model went
/// looking up the weather, answered with
/// {"tool_call":{"name":"web_search","arguments":{"query":"…"}}} — a JSON envelope.
/// Detection looked only at DSML, so the native tool was never named, the generic
/// markup repair went out, the model asked again, and the turn ended in a 502.
/// </summary>
public sealed class NativeToolRepairTests
{
    private static readonly ImmutableHashSet<string> ClientTools =
        ImmutableHashSet.Create("read", "write", "glob", "bash", "edit", "grep", "check");

    // Search on or off comes from the model the request names, not from a flag here.
    private static ChatCompletionService.Prepared Prepared(bool searchEnabled = true) => ChatCompletionService.Prepare(
        new ChatCompletionRequest
        {
            Model = searchEnabled ? "deepseek-v4-flash" : "deepseek-v4-flash-nosearch",
            Messages = [new ChatMessage { Role = "user", Content = JsonSerializer.SerializeToElement("сделай") }],
            Tools = [Tool("read"), Tool("write")],
        },
        new AppConfig().Normalize());

    private static ToolDefinition Tool(string name) => new()
    {
        Type = "function",
        Function = new ToolDefinitionFunction { Name = name },
    };

    private static UpstreamResult Reply(string content) => new(content, "", "m", "stop");

    private const string JsonWebSearch =
        """{"tool_call":{"name":"web_search","arguments":{"query":"погода"}}}""";

    private const string DsmlWebSearch =
        "<｜DSML｜tool_calls><｜DSML｜invoke name=\"web_search\">" +
        "<｜DSML｜parameter name=\"query\" string=\"true\">погода</｜DSML｜parameter>" +
        "</｜DSML｜invoke></｜DSML｜tool_calls>";

    [Fact]
    public void AJsonEnvelopeCallToANativeToolGetsTheNativeRepair()
    {
        var resolution = ChatCompletionService.Resolve(Prepared(), Reply(JsonWebSearch));

        Assert.Null(resolution.Result);
        Assert.NotNull(resolution.RetryPrompt);

        // The repair has to name the tool. Without the name the model asks again.
        Assert.Contains("web_search", resolution.RetryPrompt!);
    }

    [Fact]
    public void ADsmlCallToANativeToolGetsTheSameRepair()
    {
        var resolution = ChatCompletionService.Resolve(Prepared(), Reply(DsmlWebSearch));

        Assert.NotNull(resolution.RetryPrompt);
        Assert.Contains("web_search", resolution.RetryPrompt!);
    }

    [Fact]
    public void TheTwoEnvelopesAreTreatedIdentically()
    {
        var json = ChatCompletionService.Resolve(Prepared(), Reply(JsonWebSearch));
        var dsml = ChatCompletionService.Resolve(Prepared(), Reply(DsmlWebSearch));

        Assert.Equal(json.RetryPrompt, dsml.RetryPrompt);
    }

    [Fact]
    public void TheRepairSaysSearchIsAlreadyBuiltIn()
    {
        // The model's error was thinking it needed to look something up. Telling it
        // that search is on is the part that actually stops the retry.
        var withSearch = ChatCompletionService.Resolve(Prepared(searchEnabled: true), Reply(JsonWebSearch));
        var withoutSearch = ChatCompletionService.Resolve(Prepared(searchEnabled: false), Reply(JsonWebSearch));

        Assert.Contains("Web Search is already enabled", withSearch.RetryPrompt!);
        Assert.DoesNotContain("already enabled", withoutSearch.RetryPrompt!);
    }

    [Fact]
    public void TheRepairNamesTheExactToolRatherThanAguessing()
    {
        var resolution = ChatCompletionService.Resolve(Prepared(),
            Reply("""{"tool_call":{"name":"execute_code","arguments":{"code":"x"}}}"""));

        Assert.Contains("execute_code", resolution.RetryPrompt!);
    }

    /// <summary>The other half: a call the client really did offer must survive.</summary>
    [Fact]
    public void ACallToAToolTheClientOffersIsNotTreatedAsNative()
    {
        var resolution = ChatCompletionService.Resolve(Prepared(),
            Reply("""{"tool_call":{"name":"write","arguments":{"path":"a.txt","content":"x"}}}"""));

        Assert.NotNull(resolution.Result);
        Assert.Null(resolution.RetryPrompt);
        Assert.Equal("write", resolution.Result!.ToolCalls[0].Name);
    }

    /// <summary>
    /// A tool that is neither native nor offered is still unknown, and must not be
    /// mislabelled as a built-in the model should stop using.
    /// </summary>
    [Fact]
    public void AnUnknownNonNativeToolIsNotCalledANativeOne()
    {
        var resolution = ChatCompletionService.Resolve(Prepared(),
            Reply("""{"tool_call":{"name":"discard_everything","arguments":{}}}"""));

        Assert.NotNull(resolution.RetryPrompt);
        Assert.DoesNotContain("discard_everything", resolution.RetryPrompt!);
    }

    [Fact]
    public void GenuinelyMalformedMarkupStillGetsTheMarkupRepair()
    {
        const string broken = """{"tool_call":{"name":"read","arguments":{""";

        var resolution = ChatCompletionService.Resolve(Prepared(), Reply(broken));

        Assert.NotNull(resolution.RetryPrompt);
        Assert.Contains("incomplete tool-call markup", resolution.RetryPrompt!);
    }

    [Fact]
    public void PlainProseIsLeftAlone()
    {
        var resolution = ChatCompletionService.Resolve(Prepared(), Reply("Вот краткий ответ по вашему вопросу."));

        Assert.NotNull(resolution.Result);
        Assert.Null(resolution.RetryPrompt);
    }
}