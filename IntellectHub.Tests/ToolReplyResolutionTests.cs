using System.Collections.Immutable;
using System.Text.Json;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// The reported bug: DeepSeek answered with a doubled-bar DSML block and the raw
/// markup was handed to the client as assistant text. These tests pin the rule that
/// tool markup is either turned into a real tool call or held back for repair, and
/// is never delivered as the answer.
/// </summary>
public sealed class ToolReplyResolutionTests
{
    private const string ReportedReply =
        "I'll create a fun racing game featuring a blue tractor. Let me first check the working directory.\n\n" +
        "<｜｜DSML｜｜ calls>\n" +
        "<｜｜DSML｜｜ invoke name=\"run_commands\">\n" +
        "<｜｜DSML｜｜ parameter name=\"command\" string=\"true\">dir /a \"c:\\research\\games\\RacingBlueTraktor\"</｜｜DSML｜｜ parameter>\n" +
        "</｜｜DSML｜｜ invoke>\n" +
        "</｜｜DSML｜｜ calls>";

    private static ChatCompletionService.Prepared WithTools(params string[] names) =>
        new(
            "prompt",
            new ModelConfig { ModelType = "default", RealModel = "test", Capabilities = new ModelCapabilities() },
            [],
            names.ToImmutableHashSet());

    private static ChatCompletionService.Prepared ChatOnly() => WithTools();

    private static UpstreamResult Reply(string content) => new(content, "", "msg-1", "stop");

    private static void AssertNoLeak(ChatCompletionService.Resolution resolution)
    {
        Assert.NotNull(resolution.RetryPrompt);
        Assert.Null(resolution.Result);
    }

    [Fact]
    public void ReportedDsmlReply_IsNeverReturnedAsAssistantText()
    {
        var resolution = ChatCompletionService.Resolve(ChatOnly(), Reply(ReportedReply));

        AssertNoLeak(resolution);
    }

    [Fact]
    public void ReportedDsmlReply_IsNeverReturnedAsAssistantText_EvenWhenToolsAreOffered()
    {
        var resolution = ChatCompletionService.Resolve(
            WithTools("write_file", "read_file", "list_dir"), Reply(ReportedReply));

        AssertNoLeak(resolution);
    }

    [Fact]
    public void NativeToolMarkup_IsHeldBackForARepairPrompt()
    {
        const string reply = "<｜DSML｜tool_calls><｜DSML｜invoke name=\"execute_code\">" +
                             "<｜DSML｜parameter name=\"code\" string=\"true\">print(1)</｜DSML｜parameter>" +
                             "</｜DSML｜invoke></｜DSML｜tool_calls>";

        var resolution = ChatCompletionService.Resolve(ChatOnly(), Reply(reply));

        AssertNoLeak(resolution);
        Assert.Equal(ToolPrompts.NativeToolRepairPrompt(false, "execute_code"), resolution.RetryPrompt);
    }

    [Fact]
    public void TheRepairPrompt_NamesTheNativeToolTheModelReachedFor()
    {
        // Naming run_commands back at the model is what stops it asking again.
        const string reply = "<｜｜DSML｜｜ calls><｜｜DSML｜｜ invoke name=\"run_commands\">" +
                             "<｜｜DSML｜｜ parameter name=\"command\" string=\"true\">dir /a</｜｜DSML｜｜ parameter>" +
                             "</｜｜DSML｜｜ invoke></｜｜DSML｜｜ calls>";

        var resolution = ChatCompletionService.Resolve(ChatOnly(), Reply(reply));

        AssertNoLeak(resolution);
        Assert.Contains("run_commands", resolution.RetryPrompt);
        Assert.Contains("do not call them again", resolution.RetryPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedMarkup_IsHeldBackForTheMarkupRepairPrompt()
    {
        const string reply = "<｜DSML｜tool_calls><｜DSML｜invoke name=\"write_file\">" +
                             "<｜DSML｜parameter name=\"path\">/tmp/a</｜DSML｜parameter>" +
                             "GARBAGE</｜DSML｜invoke></｜DSML｜tool_calls>";

        var resolution = ChatCompletionService.Resolve(WithTools("write_file"), Reply(reply));

        AssertNoLeak(resolution);
        Assert.Equal(ToolPrompts.MalformedMarkupRepairPrompt, resolution.RetryPrompt);
    }

    [Fact]
    public void AValidCallToAnOfferedTool_BecomesAToolCall()
    {
        const string reply = "<｜DSML｜tool_calls><｜DSML｜invoke name=\"write_file\">" +
                             "<｜DSML｜parameter name=\"path\" string=\"true\">index.html</｜DSML｜parameter>" +
                             "<｜DSML｜parameter name=\"content\" string=\"true\"><h1>Tractor</h1></｜DSML｜parameter>" +
                             "</｜DSML｜invoke></｜DSML｜tool_calls>";

        var resolution = ChatCompletionService.Resolve(WithTools("write_file", "read_file"), Reply(reply));

        Assert.Null(resolution.RetryPrompt);
        var result = Assert.IsType<CompletionResult>(resolution.Result);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("write_file", call.Name);
        Assert.Equal("tool_calls", result.FinishReason);
        Assert.Equal("", result.Content);
        Assert.DoesNotContain("DSML", result.Content);
    }

    [Fact]
    public void ACallToAToolTheClientNeverOffered_IsHeldBack()
    {
        const string reply = "<｜DSML｜tool_calls><｜DSML｜invoke name=\"discard_everything\">" +
                             "<｜DSML｜parameter name=\"scope\" string=\"true\">all</｜DSML｜parameter>" +
                             "</｜DSML｜invoke></｜DSML｜tool_calls>";

        var resolution = ChatCompletionService.Resolve(WithTools("read_file"), Reply(reply));

        AssertNoLeak(resolution);
    }

    [Fact]
    public void PlainProse_IsPassedThroughUnchanged()
    {
        const string reply = "Here is the game. Save it as index.html and open it in a browser.";

        var resolution = ChatCompletionService.Resolve(ChatOnly(), Reply(reply));

        Assert.Null(resolution.RetryPrompt);
        var result = Assert.IsType<CompletionResult>(resolution.Result);
        Assert.Equal(reply, result.Content);
        Assert.Empty(result.ToolCalls);
        Assert.Equal("stop", result.FinishReason);
    }

    [Fact]
    public void ACodeDump_IsHeldBackWhenToolsAreOffered()
    {
        var dump = "```html\n<h1>Tractor</h1>\n" + new string('x', 400) + "\n```";

        var resolution = ChatCompletionService.Resolve(WithTools("write_file"), Reply(dump));

        AssertNoLeak(resolution);
        Assert.Equal(ToolPrompts.CodeDumpRepairPrompt, resolution.RetryPrompt);
    }

    [Fact]
    public void ACodeDump_IsHarmlessWhenNoToolsAreOffered()
    {
        var dump = "```html\n<h1>Tractor</h1>\n" + new string('x', 400) + "\n```";

        var resolution = ChatCompletionService.Resolve(ChatOnly(), Reply(dump));

        Assert.Null(resolution.RetryPrompt);
        Assert.Equal(dump, Assert.IsType<CompletionResult>(resolution.Result).Content);
    }

    [Fact]
    public void TheToolManifest_ReachesThePromptWhenToolsAreOffered()
    {
        var request = new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage { Role = "user", Content = JsonSerializer.SerializeToElement("write the game") },
            ],
            Tools =
            [
                new ToolDefinition
                {
                    Type = "function",
                    Function = new ToolDefinitionFunction
                    {
                        Name = "write_file",
                        Description = "Write a file",
                        Parameters = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}"),
                    },
                },
            ],
        };

        var prepared = ChatCompletionService.Prepare(request, new AppConfig());

        Assert.Contains("write_file", prepared.Prompt);
        Assert.Contains("write the game", prepared.Prompt);
        Assert.Contains("--- TOOLS ---", prepared.Prompt);
        Assert.Contains("--- TOOL REMINDER ---", prepared.Prompt);
        Assert.Equal(["write_file"], prepared.AllowedNames);
    }

    [Fact]
    public void NoManifestIsAddedWhenTheClientOffersNoTools()
    {
        var request = new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Role = "user", Content = JsonSerializer.SerializeToElement("hi") }],
        };

        var prepared = ChatCompletionService.Prepare(request, new AppConfig());

        Assert.DoesNotContain("--- TOOLS ---", prepared.Prompt);
        Assert.Empty(prepared.AllowedNames);
    }

    [Fact]
    public void TheClientPayload_CarriesTheToolCallInsteadOfRawMarkup()
    {
        var result = new CompletionResult
        {
            Content = "",
            MessageId = "m1",
            FinishReason = "tool_calls",
            ToolCalls = [new ParsedToolCall("write_file", "{\"path\":\"index.html\"}")],
        };

        var json = ChatCompletionsHandler.BuildCompletionPayload(
            "chatcmpl-m1", "deepseek-v4-flash", result, ["call_abc"]);

        using var doc = JsonDocument.Parse(json);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var message = choice.GetProperty("message");

        Assert.Equal(JsonValueKind.Null, message.GetProperty("content").ValueKind);
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());

        var call = message.GetProperty("tool_calls")[0];
        Assert.Equal("call_abc", call.GetProperty("id").GetString());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal("write_file", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"path\":\"index.html\"}", call.GetProperty("function").GetProperty("arguments").GetString());
        Assert.DoesNotContain("DSML", json);
    }

    [Fact]
    public void RunCommands_IsTreatedAsADeepSeekNativeTool()
    {
        // DeepSeek's own web tool. If it is not classified as native it leaks
        // through as a gateway tool call and the client cannot execute it.
        Assert.True(ToolCallParser.IsDeepSeekNativeTool("run_commands"));
        Assert.True(ToolCallParser.IsDeepSeekNativeTool("run_command"));
        Assert.True(ToolCallParser.IsDeepSeekNativeTool("write_file"));
        Assert.True(ToolCallParser.IsDeepSeekNativeTool("browser_action"));
    }

    [Fact]
    public void ANativeToolNameTheClientActuallyOffers_IsStillHonoured()
    {
        // Native-ness must only matter when the client did NOT offer the name,
        // otherwise every client with a write_file tool would break.
        const string reply = "<｜DSML｜tool_calls><｜DSML｜invoke name=\"write_file\">" +
                             "<｜DSML｜parameter name=\"path\" string=\"true\">a.html</｜DSML｜parameter>" +
                             "</｜DSML｜invoke></｜DSML｜tool_calls>";

        var resolution = ChatCompletionService.Resolve(WithTools("write_file"), Reply(reply));

        Assert.Null(resolution.RetryPrompt);
        Assert.Equal("write_file", Assert.Single(Assert.IsType<CompletionResult>(resolution.Result).ToolCalls).Name);
    }

    [Fact]
    public void ARepairReplyAskingForANativeTool_IsNeverEmittedAsAToolCall()
    {
        // This is the loop Cline hit: the repair round answered with run_commands.
        // The gateway cannot run native DeepSeek tools, so instead of a 502 it
        // ignores the call and delivers stripped text — the model adapts.
        const string retryReply = "{\"tool_call\":{\"name\":\"run_commands\",\"arguments\":{\"command\":\"dir /a\"}}}";

        var resolution = ChatCompletionService.ResolveRepair(ChatOnly(), Reply(retryReply));

        Assert.Null(resolution.RetryPrompt);
        Assert.NotNull(resolution.Result);
        Assert.False(resolution.Result!.HasToolCalls);
        Assert.Equal(string.Empty, resolution.Result.Content);
    }

    [Fact]
    public void ARepairReplyAskingForATrulyForeignTool_IsAlsoNotEmitted()
    {
        // Foreign tool (delete_everything) cannot be executed. Instead of HTTP 502,
        // the gateway strips the markup and delivers empty text.
        const string retryReply = "{\"tool_call\":{\"name\":\"delete_everything\",\"arguments\":{}}}";

        var resolution = ChatCompletionService.ResolveRepair(ChatOnly(), Reply(retryReply));

        Assert.Null(resolution.RetryPrompt);
        Assert.NotNull(resolution.Result);
        Assert.False(resolution.Result.HasToolCalls);
        Assert.Equal(string.Empty, resolution.Result.Content);
    }

    [Fact]
    public void ARepairReplyWithPlainProse_IsUsedAsTheAnswer()
    {
        const string retryReply = "Here is the game as a single HTML file.";

        var resolution = ChatCompletionService.ResolveRepair(ChatOnly(), Reply(retryReply));

        Assert.False(resolution.Unusable);
        Assert.Equal(retryReply, Assert.IsType<CompletionResult>(resolution.Result).Content);
    }

    [Fact]
    public void TheClientPayload_ReturnsPlainTextWhenThereIsNoToolCall()
    {
        var result = new CompletionResult { Content = "Here is the game.", MessageId = "m2", FinishReason = "stop" };

        var json = ChatCompletionsHandler.BuildCompletionPayload(
            "chatcmpl-m2", "deepseek-v4-flash", result, []);

        using var doc = JsonDocument.Parse(json);
        var choice = doc.RootElement.GetProperty("choices")[0];

        Assert.Equal("Here is the game.", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.Equal(JsonValueKind.Null, choice.GetProperty("message").GetProperty("tool_calls").ValueKind);
    }
}