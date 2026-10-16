using System.Collections.Immutable;
using System.Text.Json;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Port of the Node gateway's DSML suite. The bar is the same: well-formed markup
/// becomes a tool call, and anything else is rejected rather than executed.
/// </summary>
public sealed class DsmlToolCallTests
{
    private static readonly ImmutableHashSet<string> NoTools = ImmutableHashSet<string>.Empty;

    private static JsonElement Text(string value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement Args(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    // ---- canonical form ---------------------------------------------------------

    [Fact]
    public void ParseToolCall_ConvertsCanonicalDsml_IntoAnOpenAiToolCall()
    {
        const string text = "<｜DSML｜tool_calls>" +
                            "<｜DSML｜invoke name=\"execute_code\">" +
                            "<｜DSML｜parameter name=\"code\" string=\"true\">print(\"ok\")</｜DSML｜parameter>" +
                            "<｜DSML｜parameter name=\"timeout\" string=\"false\">30</｜DSML｜parameter>" +
                            "<｜DSML｜parameter name=\"capture\" string=\"false\">true</｜DSML｜parameter>" +
                            "</｜DSML｜invoke>" +
                            "</｜DSML｜tool_calls>";

        var call = ToolCallParser.ParseToolCall(text);

        Assert.NotNull(call);
        Assert.Equal("execute_code", call!.Name);
        Assert.Equal("{\"code\":\"print(\\\"ok\\\")\",\"timeout\":30,\"capture\":true}", call.Arguments);
    }

    [Fact]
    public void ParseToolCall_ReadsTheDoubledBarWebVariant()
    {
        const string text = "<｜｜DSML｜｜ Tool Calls>" +
                            "<｜｜DSML｜｜ name=\"web_search\">{\"query\":\"DeepSeek DSML\"}" +
                            "</｜｜DSML｜｜ Tool Calls>";

        var call = ToolCallParser.ParseToolCall(text);

        Assert.NotNull(call);
        Assert.Equal("web_search", call!.Name);
        Assert.Equal("{\"query\":\"DeepSeek DSML\"}", call.Arguments);
    }

    [Fact]
    public void ListDsmlToolCalls_HandlesStackedNativeMarkup()
    {
        const string text = "<｜｜DSML｜｜ Tool Calls> <｜｜DSML｜｜ name=\"execute_code\">{\"code\":\"print(1)\"}" +
                            "<｜｜DSML｜｜ name=\"web_search\">{\"query\":\"Unity player controller\"}";

        var calls = ToolCallParser.ListDsmlToolCalls(text);

        Assert.Equal(["execute_code", "web_search"], calls.Select(call => call.Name));
    }

    [Fact]
    public void ParseToolCall_AcceptsTheBareDoubledBarCallsWrapper()
    {
        // The exact shape DeepSeek's web output produces for the reported bug.
        const string text = "I'll create a fun racing game.\n\n" +
                            "<｜｜DSML｜｜ calls>\n" +
                            "<｜｜DSML｜｜ invoke name=\"run_commands\">\n" +
                            "<｜｜DSML｜｜ parameter name=\"command\" string=\"true\">dir /a</｜｜DSML｜｜ parameter>\n" +
                            "</｜｜DSML｜｜ invoke>\n" +
                            "</｜｜DSML｜｜ calls>";

        var call = ToolCallParser.ParseToolCall(text);

        Assert.NotNull(call);
        Assert.Equal("run_commands", call!.Name);
        Assert.Equal("{\"command\":\"dir /a\"}", call.Arguments);
    }

    [Fact]
    public void ParseToolCall_AcceptsCaretEscapedAngleBrackets()
    {
        const string text = "<|DSML|tool_calls><|DSML|invoke name=\"read_file\"></|DSML|invoke></|DSML|tool_calls>";

        var call = ToolCallParser.ParseToolCall(text);

        Assert.NotNull(call);
        Assert.Equal("read_file", call!.Name);
    }

    [Fact]
    public void ParseToolCall_AcceptsFullWidthPunctuation()
    {
        const string text =
            "＜｜DSML｜Tool Calls＞＜｜DSML｜Invoke name=“read_file”＞＜｜DSML｜Parameter name=“path”＞/tmp/c＜/｜DSML｜Parameter＞＜/｜DSML｜Invoke＞＜/｜DSML｜Tool Calls＞";

        var call = ToolCallParser.ParseToolCall(text);

        Assert.NotNull(call);
        Assert.Equal("read_file", call!.Name);
        Assert.Equal("{\"path\":\"/tmp/c\"}", call.Arguments);
    }

    [Fact]
    public void ParseToolCall_DecodesCdataParameters()
    {
        const string text = "<｜DSML｜tool_calls><｜DSML｜invoke name=\"write_file\">" +
                            "<｜DSML｜parameter name=\"body\"><![CDATA[if (a < b && c > d) {}]]></｜DSML｜parameter>" +
                            "</｜DSML｜invoke></｜DSML｜tool_calls>";

        var call = ToolCallParser.ParseToolCall(text);

        Assert.NotNull(call);
        Assert.Equal("if (a < b && c > d) {}", Args(call!.Arguments).GetProperty("body").GetString());
    }

    // ---- refusals ---------------------------------------------------------------

    [Fact]
    public void ParseToolCall_RefusesIncompleteDsml()
    {
        const string text = "<｜DSML｜tool_calls>" +
                            "<｜DSML｜invoke name=\"execute_code\">" +
                            "<｜DSML｜parameter name=\"code\"><!DOCTYPE foo>" +
                            "</｜DSML｜parameter>" +
                            "</｜DSML｜tool_calls>";

        Assert.Null(ToolCallParser.ParseToolCall(text));
    }

    [Fact]
    public void ParseToolCall_NeverExecutesABareInvokeWithoutAWrapper()
    {
        const string text =
            "<｜｜DSML｜｜ invoke name=\"execute_code\"><｜｜DSML｜｜ parameter name=\"code\" string=\"true\">danger()</｜｜DSML｜｜ parameter></｜｜DSML｜｜ invoke>";

        Assert.Null(ToolCallParser.ParseToolCall(text));
        Assert.Empty(ToolCallParser.ListDsmlToolCalls(text));
    }

    [Fact]
    public void ParseToolCall_RejectsPartiallyConsumedParameters()
    {
        // Text between two <parameter> tags means the body was not tiled exactly.
        const string text = "<｜DSML｜tool_calls><｜DSML｜invoke name=\"execute_code\">" +
                            "<｜DSML｜parameter name=\"code\" string=\"true\">print(1)</｜DSML｜parameter>" +
                            "leftover" +
                            "<｜DSML｜parameter name=\"x\" string=\"true\">1</｜DSML｜parameter>" +
                            "</｜DSML｜invoke></｜DSML｜tool_calls>";

        Assert.Null(ToolCallParser.ParseToolCall(text));
    }

    [Fact]
    public void ParseToolCall_RejectsDuplicateParameterNames()
    {
        const string text = "<｜DSML｜tool_calls><｜DSML｜invoke name=\"run\">" +
                            "<｜DSML｜parameter name=\"cmd\" string=\"true\">a</｜DSML｜parameter>" +
                            "<｜DSML｜parameter name=\"cmd\" string=\"true\">b</｜DSML｜parameter>" +
                            "</｜DSML｜invoke></｜DSML｜tool_calls>";

        Assert.Null(ToolCallParser.ParseToolCall(text));
    }

    [Fact]
    public void ParseToolCall_RejectsOverlongMarkupInBoundedTime()
    {
        var oversized = "<|DSML|tool_calls>" + new string('x', 256 * 1024) + "</|DSML|tool_calls>";

        var started = DateTime.UtcNow;
        Assert.Null(ToolCallParser.ParseToolCall(oversized));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ParseToolCall_RejectsMalformedQuotedTagsInBoundedTime()
    {
        var malformed = "<｜DSML｜tool_calls><｜DSML｜invoke name=\"unterminated " + new string('x', 4096);

        var started = DateTime.UtcNow;
        Assert.Null(ToolCallParser.ParseToolCall(malformed));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ParseToolCall_RejectsAnUnsafeToolName()
    {
        const string text = "<｜DSML｜tool_calls><｜DSML｜invoke name=\"rm -rf /\">" +
                            "<｜DSML｜parameter name=\"cmd\" string=\"true\">x</｜DSML｜parameter>" +
                            "</｜DSML｜invoke></｜DSML｜tool_calls>";

        Assert.Null(ToolCallParser.ParseToolCall(text));
    }

    // ---- JSON envelopes ---------------------------------------------------------

    [Fact]
    public void ParseToolCall_ReadsTheJsonToolCallEnvelope()
    {
        var call = ToolCallParser.ParseToolCall("{\"tool_call\":{\"name\":\"list_dir\",\"arguments\":{\"path\":\".\"}}}");

        Assert.NotNull(call);
        Assert.Equal("list_dir", call!.Name);
        Assert.Equal("{\"path\":\".\"}", call.Arguments);
    }

    [Fact]
    public void ParseToolCall_ReadsAFencedJsonEnvelope()
    {
        var call = ToolCallParser.ParseToolCall("```json\n{\"tool_call\":{\"name\":\"list_dir\",\"arguments\":{}}}\n```");

        Assert.NotNull(call);
        Assert.Equal("list_dir", call!.Name);
    }

    [Fact]
    public void ParseToolCall_IgnoresBareObjectsThatAreNotToolEnvelopes()
    {
        Assert.Null(ToolCallParser.ParseToolCall("{\"name\":\"list_dir\",\"arguments\":{}}"));
    }

    [Fact]
    public void ParseToolCall_ReadsTheLegacyToolCallMarker()
    {
        var call = ToolCallParser.ParseToolCall("TOOL_CALL: list_dir {\"path\":\".\"}");

        Assert.NotNull(call);
        Assert.Equal("list_dir", call!.Name);
    }

    [Fact]
    public void ParseToolCall_DecodesStringEncodedArguments()
    {
        // OpenAI clients send arguments as a JSON string, not an object.
        const string text = "{\"tool_calls\":[{\"type\":\"function\",\"function\":{\"name\":\"read_file\"," +
                            "\"arguments\":\"{\\\"path\\\":\\\"/tmp/b\\\"}\"}}]}";

        var call = ToolCallParser.ParseToolCall(text);

        Assert.NotNull(call);
        Assert.Equal("read_file", call!.Name);
        Assert.Equal("{\"path\":\"/tmp/b\"}", call.Arguments);
    }

    [Fact]
    public void ParseToolCall_RejectsArgumentsThatAreNotJson()
    {
        Assert.Null(ToolCallParser.ParseToolCall("{\"tool_call\":{\"name\":\"read_file\",\"arguments\":\"not-json\"}}"));
    }

    [Fact]
    public void ParseToolCall_RejectsNonObjectArguments()
    {
        Assert.Null(ToolCallParser.ParseToolCall("{\"tool_call\":{\"name\":\"read_file\",\"arguments\":[1,2]}}"));
        Assert.Null(ToolCallParser.ParseToolCall("{\"tool_call\":{\"name\":\"read_file\",\"arguments\":42}}"));
    }

    [Fact]
    public void ParseToolCall_RejectsAnEnvelopeWithMoreThanOneCall()
    {
        const string text = "{\"tool_calls\":[{\"function\":{\"name\":\"read_file\",\"arguments\":\"{}\"}}," +
                            "{\"function\":{\"name\":\"write_file\",\"arguments\":\"{}\"}}]}";

        Assert.Null(ToolCallParser.ParseToolCall(text));
    }

    // ---- client-side filtering --------------------------------------------------

    [Fact]
    public void CollectAgentToolCalls_KeepsOnlyToolsTheClientOffered()
    {
        const string text = "{\"tool_calls\":[{\"name\":\"write_file\",\"arguments\":{}}," +
                            "{\"name\":\"discard_everything\",\"arguments\":{}}]}";

        var calls = ToolCallParser.CollectAgentToolCalls(text,
            ImmutableHashSet.Create("write_file", "read_file"));

        Assert.Equal("write_file", Assert.Single(calls).Name);
    }

    [Fact]
    public void SelectAgentToolCall_AcceptsAnyCallForAChatOnlyClient()
    {
        const string text = "<｜DSML｜tool_calls><｜DSML｜invoke name=\"execute_code\"></｜DSML｜invoke></｜DSML｜tool_calls>";

        var call = ToolCallParser.SelectAgentToolCall(text, NoTools);

        Assert.Equal("execute_code", Assert.IsType<ParsedToolCall>(call).Name);
    }

    [Fact]
    public void SelectAgentToolCall_RefusesUnknownToolsWhenToolsWereOffered()
    {
        const string text = "<｜DSML｜tool_calls><｜DSML｜invoke name=\"discard_everything\"></｜DSML｜invoke></｜DSML｜tool_calls>";

        Assert.Null(ToolCallParser.SelectAgentToolCall(text, ImmutableHashSet.Create("read_file")));
    }

    [Fact]
    public void CollectAgentToolCalls_CapsTheNumberOfCalls()
    {
        var calls = Enumerable.Range(0, 20)
            .Select(index => $"{{\"tool_call\":{{\"name\":\"read_file\",\"arguments\":{{\"i\":{index}}}}}}}");
        var text = "[" + string.Join(",", calls) + "]";

        var parsed = ToolCallParser.CollectAgentToolCalls(text, ImmutableHashSet.Create("read_file"));

        Assert.Equal(ToolCallParser.MaxAgentToolCalls, parsed.Count);
    }

    // ---- shape detection --------------------------------------------------------

    [Fact]
    public void LooksLikeToolCallMarkup_DetectsEveryVariant()
    {
        Assert.True(ToolCallParser.LooksLikeToolCallMarkup("<｜DSML｜tool_calls>"));
        Assert.True(ToolCallParser.LooksLikeToolCallMarkup("TOOL_CALL: list_dir"));
        Assert.True(ToolCallParser.LooksLikeToolCallMarkup("{\"tool_call\":{}}"));
        Assert.False(ToolCallParser.LooksLikeToolCallMarkup("Here is the game you asked for."));
    }

    [Fact]
    public void IsDeepSeekNativeTool_RecognisesTheUpstreamBuiltins()
    {
        Assert.True(ToolCallParser.IsDeepSeekNativeTool("execute_code"));
        Assert.True(ToolCallParser.IsDeepSeekNativeTool("web_search"));
        Assert.True(ToolCallParser.IsDeepSeekNativeTool("browser_search"));

        // A gateway tool that DeepSeek does not own must stay non-native.
        Assert.False(ToolCallParser.IsDeepSeekNativeTool("my_custom_tool"));
        Assert.False(ToolCallParser.IsDeepSeekNativeTool("create_plan"));
        Assert.False(ToolCallParser.IsDeepSeekNativeTool(null));
    }

    [Fact]
    public void LooksLikeCodeDumpInsteadOfTool_SpotsSourceBlocks()
    {
        var dump = "```html\n" + new string('x', 500) + "\n```";

        Assert.True(ToolCallParser.LooksLikeCodeDumpInsteadOfTool(dump));
        Assert.False(ToolCallParser.LooksLikeCodeDumpInsteadOfTool("Here is the game you asked for."));
    }
}