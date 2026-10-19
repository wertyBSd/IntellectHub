using System.Text.Json;
using IntellectHub.Client;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// The approval barrier.
///
/// `ask` is the default and the reason the client exists in the shape it does:
/// a model that can run a shell command should not be able to do so by accident.
/// These tests exist mainly to pin which tools count as read-only, because that
/// list is the whole of what `writes` mode means.
/// </summary>
public sealed class ApprovalTests
{
    [Theory]
    [InlineData("read", false)]
    [InlineData("glob", false)]
    [InlineData("grep", false)]
    [InlineData("check", false)]
    public void ObservingToolsCannotChangeAnything(string tool, bool canWrite) =>
        Assert.Equal(canWrite, ToolRisk.CanWrite(tool));

    [Theory]
    [InlineData("write", true)]
    [InlineData("edit", true)]
    [InlineData("bash", true)]
    public void MutatingToolsAreAlwaysTreatedAsWrites(string tool, bool canWrite) =>
        Assert.Equal(canWrite, ToolRisk.CanWrite(tool));

    [Fact]
    public void AnUnknownToolIsTreatedAsAWrite()
    {
        // A tool added later must not be waved through by being unrecognised.
        Assert.True(ToolRisk.CanWrite("something_new"));
    }

    [Fact]
    public void AskModeStopsForEverything()
    {
        foreach (var tool in new[] { "read", "write", "glob", "bash", "edit", "grep", "check" })
            Assert.False(ToolRisk.Allowed(Approvals.Ask, tool));
    }

    [Fact]
    public void WritesModeRunsOnlyTheReadOnlyTools()
    {
        foreach (var tool in new[] { "read", "glob", "grep", "check" })
            Assert.True(ToolRisk.Allowed(Approvals.Writes, tool));

        foreach (var tool in new[] { "write", "edit", "bash", "anything_else" })
            Assert.False(ToolRisk.Allowed(Approvals.Writes, tool));
    }

    [Fact]
    public void AllModeRunsEverything()
    {
        foreach (var tool in new[] { "read", "write", "bash", "anything_else" })
            Assert.True(ToolRisk.Allowed(Approvals.All, tool));
    }

    [Theory]
    [InlineData("yes", Approvals.All)]
    [InlineData("Y", Approvals.All)]
    [InlineData("y", Approvals.All)]
    [InlineData("on", Approvals.All)]
    [InlineData("auto", Approvals.All)]
    [InlineData("all", Approvals.All)]
    [InlineData("writes", Approvals.Writes)]
    [InlineData("ASK", Approvals.Ask)]
    public void TheSpellingsPeopleUseResolve(string value, Approvals expected) =>
        Assert.Equal(expected, ToolRisk.Parse(ToolRisk.Normalize(value)));

    /// <summary>
    /// The dangerous direction. Something typed to mean "less friction" must never
    /// resolve to Ask — that is the one answer nobody wants by accident.
    /// </summary>
    [Theory]
    [InlineData("yes")]
    [InlineData("y")]
    [InlineData("on")]
    [InlineData("auto")]
    public void AskingForLessFrictionNeverResolvesToAsk(string value)
    {
        var normalized = ToolRisk.Normalize(value);

        Assert.True(
            normalized is nameof(Approvals.All) or nameof(Approvals.Writes),
            $"'{value}' resolved to {normalized}, which would silently tighten the barrier");
    }

    /// <summary>
    /// Genuine nonsense falls back to Ask, which is the safe direction: the
    /// command layer rejects the word outright rather than applying it, so the
    /// fallback is never the answer the user actually gets.
    /// </summary>
    [Fact]
    public void GenuineNonsenseFallsBackToAsking()
    {
        foreach (var value in new[] { "maybe", "sure", "go", "1" })
            Assert.Equal(nameof(Approvals.Ask), ToolRisk.Normalize(value));
    }

    [Fact]
    public void TheStoredNameRoundTripsThroughParsing()
    {
        foreach (var mode in new[] { Approvals.Ask, Approvals.Writes, Approvals.All })
            Assert.Equal(mode, ToolRisk.Parse(ToolRisk.Normalize(mode.ToString())));
    }

    [Fact]
    public void TheSettingIsPresentAndDefaultsToAsking()
    {
        var setting = Setting.Find("approvals");
        Assert.NotNull(setting);

        // Not just present: a missing default would start the client in a mode
        // nobody chose.
        Assert.Equal(nameof(Approvals.Ask), setting!.DefaultValue);
    }

    [Fact]
    public void TheApprovalSettingRejectsNonsense()
    {
        var setting = Setting.Find("approvals")!;

        Assert.Null(setting.Validate("all"));
        Assert.Null(setting.Validate("writes"));
        Assert.Null(setting.Validate("yes"));
        Assert.False(string.IsNullOrWhiteSpace(setting.Validate("sometimes")));
    }

    [Fact]
    public void SettingTheApprovalModeStoresTheCanonicalName()
    {
        var config = new ClientConfig();
        var setting = Setting.Find("approvals")!;

        setting.Write(config, "yes");

        Assert.Equal(nameof(Approvals.All), config.ApprovalMode);
        Assert.Equal(Approvals.All, config.Approvals);
    }

    [Fact]
    public void AConfigWithNoApprovalKeyAsksFirst()
    {
        // A client.json written before this setting existed must not start wide open.
        var config = JsonSerializer.Deserialize<ClientConfig>(
            """{"baseUrl":"http://127.0.0.1:9656","model":"deepseek-v4-pro"}""");

        Assert.Equal(Approvals.Ask, config!.Approvals);
    }

    [Fact]
    public void EveryModeHasSomethingToRead()
    {
        foreach (var mode in new[] { Approvals.Ask, Approvals.Writes, Approvals.All })
            Assert.False(string.IsNullOrWhiteSpace(ToolRisk.Describe(mode)));
    }
}