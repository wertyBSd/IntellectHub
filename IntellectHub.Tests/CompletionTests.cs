using IntellectHub.Client;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Tab completion and the value lists behind it. These are the shortcuts that make
/// the interactive screens usable without remembering a spelling, so the rules are
/// pinned rather than left to the console.
/// </summary>
public sealed class CompletionTests
{
    [Theory]
    [InlineData("/cle", "/clear ")]
    [InlineData("/quit", "/exit ")]
    [InlineData("/approve w", "/approve writes ")]
    [InlineData("/config temp", "/config temperature ")]
    [InlineData("/cfg mod", "/cfg model ")]
    public void TabCompletesCommandsAndTheirFirstValue(string input, string expected) =>
        Assert.Equal(expected, Completion.Next(input));

    [Theory]
    [InlineData("hello")]
    [InlineData("/zzzz")]
    [InlineData("/clear ")]
    public void TextWithNothingToAddIsLeftAlone(string input) =>
        Assert.Equal(input, Completion.Next(input));

    [Fact]
    public void SharedPrefixIsCompletedBeforeAlternatives()
    {
        // /config and /connect share "/con", so the first Tab stops there instead
        // of guessing which one was meant.
        Assert.Equal("/con", Completion.Next("/co"));
        Assert.Equal("/config", Completion.Next("/c"));
    }

    [Fact]
    public void ArgumentsResolveAliasesToTheRealCommand()
    {
        Assert.Equal(["model"], Completion.Arguments("/cfg", "mod"));
        Assert.Equal(["temperature"], Completion.Arguments("/settings", "temp"));
        Assert.Equal(["writes"], Completion.Arguments("/approve", "w"));
    }

    [Fact]
    public void CommandsWithoutAListOfferNothing()
    {
        Assert.Empty(Completion.Arguments("/clear", "x"));
        Assert.Empty(Completion.Arguments("/help", ""));
    }
}
