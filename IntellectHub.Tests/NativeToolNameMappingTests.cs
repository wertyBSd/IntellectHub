using System.Collections.Immutable;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// What happens when the model answers with a tool call the client never offered.
///
/// This is not hypothetical. A real run produced, verbatim:
///
///   {"tool_call":{"name":"write_file","arguments":{"path":"index.html","content":"…"}}}
///
/// The prompt had listed the seven real names, twice, ending with
/// "Available tools: read, write, glob, bash, edit, grep, check". The model still
/// reached for its own native name. The gateway read the envelope perfectly,
/// dropped the call because "write_file" was not in the list, then reported the
/// reply as malformed markup, spent a repair round trip re-asking the same
/// question, and answered the client with HTTP 502.
/// </summary>
public sealed class NativeToolNameMappingTests
{
    /// <summary>The exact reply from the failing run.</summary>
    private const string WriteFileReply =
        "{\"tool_call\":{\"name\":\"write_file\",\"arguments\":{\"path\":\"index.html\"," +
        "\"content\":\"<!DOCTYPE html>\\n<html lang=\\\"ru\\\">\\n<head></html>\"}}}";

    private static readonly ImmutableHashSet<string> ConsoleClientTools =
        ImmutableHashSet.Create("read", "write", "glob", "bash", "edit", "grep", "check");

    [Fact]
    public void ANativeNameTheClientAlsoOffersIsMappedOntoTheClientsName()
    {
        var call = ToolCallParser.SelectAgentToolCall(WriteFileReply, ConsoleClientTools);

        Assert.NotNull(call);
        Assert.Equal("write", call!.Name);

        // The arguments have to survive untouched: they are what the client
        // actually executes.
        Assert.Contains("index.html", call.Arguments);
        Assert.Contains("DOCTYPE html", call.Arguments);
    }

    [Theory]
    [InlineData("read_file", "read")]
    [InlineData("write_file", "write")]
    [InlineData("edit_file", "edit")]
    [InlineData("list_files", "glob")]
    [InlineData("list_dir", "glob")]
    [InlineData("run_commands", "bash")]
    [InlineData("shell_command", "bash")]
    [InlineData("search_files", "grep")]
    public void EveryNativeNameMapsWhenTheClientHasTheEquivalent(string native, string local)
    {
        var reply = $"{{\"tool_call\":{{\"name\":\"{native}\",\"arguments\":{{}}}}}}";

        Assert.Equal(local, ToolCallParser.SelectAgentToolCall(reply, ConsoleClientTools)?.Name);
    }

    [Fact]
    public void AnExactNameStillWins()
    {
        // A client that genuinely offers write_file must keep working unchanged.
        var offered = ImmutableHashSet.Create("write_file");

        Assert.Equal("write_file", ToolCallParser.SelectAgentToolCall(WriteFileReply, offered)?.Name);
    }

    [Fact]
    public void MappingOnlyAppliesWhenTheClientActuallyOffersTheTarget()
    {
        // No "write" on offer: the call must not be smuggled through under a name
        // the client never advertised.
        Assert.Null(ToolCallParser.SelectAgentToolCall(WriteFileReply, ImmutableHashSet.Create("read", "bash")));
    }

    [Fact]
    public void TheMappingIsCaseInsensitive()
    {
        var reply = "{\"tool_call\":{\"name\":\"Write_File\",\"arguments\":{}}}";

        Assert.Equal("write", ToolCallParser.SelectAgentToolCall(reply, ConsoleClientTools)?.Name);
    }

    [Fact]
    public void AToolNobodyOffersIsStillRefused()
    {
        const string reply = "{\"tool_call\":{\"name\":\"discard_everything\",\"arguments\":{\"scope\":\"all\"}}}";

        Assert.Null(ToolCallParser.SelectAgentToolCall(reply, ConsoleClientTools));
    }

    /// <summary>
    /// The error has to say what was asked for. "Malformed markup" is what the
    /// user saw while the real answer was "it called a tool you do not have".
    /// </summary>
    [Fact]
    public void AnUnresolvableCallIsStillReportedByName()
    {
        var mentioned = ToolCallParser.MentionedToolNames(
            "{\"tool_call\":{\"name\":\"discard_everything\",\"arguments\":{\"scope\":\"all\"}}}");

        Assert.Contains("discard_everything", mentioned);
    }

    [Fact]
    public void MentionedToolNamesReportsTheNativeNameNotTheMappedOne()
    {
        Assert.Equal(["write_file"], ToolCallParser.MentionedToolNames(WriteFileReply));
    }

    [Fact]
    public void MentionedToolNamesOnOrdinaryProseIsEmpty()
    {
        Assert.Empty(ToolCallParser.MentionedToolNames("Here is how you would write a file in C#."));
    }

    [Fact]
    public void TheRealReplyNoLongerLooksLikeAnUnusableFailure()
    {
        // Before the fix this pair is what produced the 502: the call was dropped
        // as unknown, yet the reply still tripped the "malformed markup" detector.
        Assert.NotNull(ToolCallParser.SelectAgentToolCall(WriteFileReply, ConsoleClientTools));
        Assert.False(ToolCallParser.LooksLikeCodeDumpInsteadOfTool(WriteFileReply));
    }
}