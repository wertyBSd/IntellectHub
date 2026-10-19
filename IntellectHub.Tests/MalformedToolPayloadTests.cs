using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Tool payloads that are right in substance and broken in form.
///
/// Asked to write a long document, the model produced exactly the tool it was
/// asked for, on exactly the right path, with the full text — and put the text
/// into a JSON string with raw newlines in it. That is not valid JSON. Strict
/// parsing discarded the whole call, the turn ended in a 502, and the work was
/// lost even though the model had already done it.
///
/// This is the common case for any "write me a big file" request, so recovering
/// from it is what makes the agent usable at all.
/// </summary>
public sealed class MalformedToolPayloadTests
{
    private static readonly ImmutableHashSet<string> ClientTools =
        ImmutableHashSet.Create("read", "write", "glob", "bash", "edit", "grep", "check");

    /// <summary>The shape that actually came back, abbreviated.</summary>
    private const string RawNewlineBody =
        "I'll generate the manual file now.\n\n" +
        "{\"tool_call\":{\"name\":\"write\",\"arguments\":{\"path\":\"docs/manual.md\",\"content\":\"# Руководство\n" +
        "\n" +
        "## Раздел 1. Введение\n" +
        "\n" +
        "Первая строка.\n" +
        "\n" +
        "Вторая строка.\"}}}\n";

    [Fact]
    public void AFileWrittenWithRawNewlinesStillProducesTheCall()
    {
        var call = ToolCallParser.SelectAgentToolCall(RawNewlineBody, ClientTools);

        Assert.NotNull(call);
        Assert.Equal("write", call!.Name);
    }

    [Fact]
    public void ThePathSurvivesTheRepair()
    {
        var call = ToolCallParser.SelectAgentToolCall(RawNewlineBody, ClientTools)!;

        Assert.Contains("docs/manual.md", call.Arguments);
    }

    /// <summary>
    /// The content is the whole point of the call. If the recovery mangles it the
    /// agent writes a broken file and the bug is worse than the original.
    /// </summary>
    [Fact]
    public void TheContentSurvivesWithItsLineBreaksIntact()
    {
        var call = ToolCallParser.SelectAgentToolCall(RawNewlineBody, ClientTools)!;

        Assert.Contains("# Руководство", call.Arguments);
        Assert.Contains("## Раздел 1. Введение", call.Arguments);
        Assert.Contains("Первая строка.", call.Arguments);
        Assert.Contains("Вторая строка.", call.Arguments);

        // The arguments must still be valid JSON with real newlines in the value.
        var args = JsonNode.Parse(call.Arguments)!.AsObject();
        var content = args["content"]!.GetValue<string>();

        Assert.Contains("\n\n## Раздел 1. Введение\n", content);
    }

    [Fact]
    public void ACrLfBodyIsRepairedToo()
    {
        const string body =
            "{\"tool_call\":{\"name\":\"write\",\"arguments\":{\"path\":\"a.txt\",\"content\":\"one\r\ntwo\"}}}";

        var call = ToolCallParser.SelectAgentToolCall(body, ClientTools);

        Assert.NotNull(call);
        Assert.Contains("one", call!.Arguments);
        Assert.Contains("two", call.Arguments);
    }

    [Fact]
    public void ATabInsideAValueIsRepaired()
    {
        const string body =
            "{\"tool_call\":{\"name\":\"write\",\"arguments\":{\"path\":\"a.txt\",\"content\":\"a\tb\"}}}";

        Assert.NotNull(ToolCallParser.SelectAgentToolCall(body, ClientTools));
    }

    /// <summary>
    /// A payload that was already valid must come through untouched — the repair
    /// must not touch bytes it has no reason to touch.
    /// </summary>
    [Fact]
    public void AnAlreadyValidPayloadIsUnchanged()
    {
        const string body =
            """{"tool_call":{"name":"write","arguments":{"path":"a.txt","content":"one\ntwo"}}}""";

        var call = ToolCallParser.SelectAgentToolCall(body, ClientTools)!;

        Assert.Equal("write", call.Name);
        Assert.Contains("\\n", call.Arguments);
    }

    [Fact]
    public void EscapedQuotesInsideTheContentSurvive()
    {
        var sb = new StringBuilder();
        sb.Append("{\"tool_call\":{\"name\":\"write\",\"arguments\":{\"path\":\"a.txt\",\"content\":\"");
        sb.Append("say \\\"hello\\\"\nthen stop\"}}}");

        var call = ToolCallParser.SelectAgentToolCall(sb.ToString(), ClientTools);

        Assert.NotNull(call);
        Assert.Contains("hello", call!.Arguments);
        Assert.Contains("then stop", call.Arguments);
    }

    [Fact]
    public void AStructureWithNoStringLiteralIsStillRejected()
    {
        // Genuinely broken JSON must not be coerced into a call.
        const string body = """{"tool_call":{"name":"write","arguments":{"path":""}""";

        Assert.Null(ToolCallParser.SelectAgentToolCall(body, ClientTools));
    }

    [Fact]
    public void ProseAroundTheCallDoesNotBreakIt()
    {
        const string body =
            "Сначала посмотрю структуру.\n\n" +
            "{\"tool_call\":{\"name\":\"write\",\"arguments\":{\"path\":\"a.md\",\"content\":\"# T\n\nтекст\"}}}\n\n" +
            "Готово.";

        var call = ToolCallParser.SelectAgentToolCall(body, ClientTools);

        Assert.NotNull(call);
        Assert.Equal("write", call!.Name);
    }

    // ---- "none" is not a tool name --------------------------------------------

    /// <summary>
    /// The model asked for a tool literally named "none" and the user was told
    /// "DeepSeek called none, which this client does not offer". That is the model
    /// declining to pick a tool, not naming one.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("None")]
    [InlineData("null")]
    [InlineData("nil")]
    [InlineData("noop")]
    [InlineData("no_tool")]
    public void APlaceholderNameIsNotAToolCall(string name)
    {
        var body = "{\"tool_call\":{\"name\":\"" + name + "\",\"arguments\":{}}}";

        Assert.Null(ToolCallParser.SelectAgentToolCall(body, ClientTools));
        Assert.Empty(ToolCallParser.MentionedToolNames(body));
    }

    [Fact]
    public void ARealToolCalledNoneByTheClientStillWorks()
    {
        // If the client really offers a tool with that name, honour it.
        var offered = ImmutableHashSet.Create("none");

        Assert.NotNull(ToolCallParser.SelectAgentToolCall(
            """{"tool_call":{"name":"none","arguments":{}}}""", offered));
    }
}