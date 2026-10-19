using IntellectHub.Client;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// The name shown where the address used to be.
///
/// Two header labels: which gateway this is, and which project. Both are read
/// constantly, so both had to stop being long strings of characters.
/// </summary>
public sealed class HeaderLabelTests
{
    [Theory]
    [InlineData(@"C:\research\games\RacingBlueTraktor", "games/RacingBlueTraktor")]
    [InlineData(@"C:\git\IntellectHub", "git/IntellectHub")]
    [InlineData(@"C:\work", "work")]
    [InlineData(@"C:\", "C:\\")]
    [InlineData("/home/alex/projects/site", "projects/site")]
    [InlineData("relative/path/leaf", "path/leaf")]
    public void OnlyTheEndOfThePathIsShown(string root, string expected) =>
        Assert.Equal(expected, Banner.ProjectLabel(root));

    [Fact]
    public void ATrailingSeparatorDoesNotProduceAnEmptySegment()
    {
        Assert.Equal("games/RacingBlueTraktor", Banner.ProjectLabel(@"C:\research\games\RacingBlueTraktor\"));
    }

    [Fact]
    public void AnEmptyProjectStillReadsAsSomething()
    {
        Assert.False(string.IsNullOrWhiteSpace(Banner.ProjectLabel("")));
        Assert.False(string.IsNullOrWhiteSpace(Banner.ProjectLabel("   ")));
    }

    [Fact]
    public void TwoFoldersWithTheSameNameStayDistinguishable()
    {
        // The parent is kept precisely so this case does not become ambiguous.
        var a = Banner.ProjectLabel(@"C:\one\shared");
        var b = Banner.ProjectLabel(@"C:\two\shared");

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// A named gateway is called by its name. The address is still shown, but it
    /// is no longer the only thing identifying it.
    /// </summary>
    [Fact]
    public void ANamedServerIsLabelledByName()
    {
        var config = new ClientConfig { ServerName = "work", BaseUrl = "http://10.0.0.5:9656" };

        Assert.Equal("work", config.ServerLabel);
    }

    [Fact]
    public void AnAdHocAddressIsLabelledAsSuch()
    {
        var config = new ClientConfig { BaseUrl = "http://10.0.0.5:9656" };

        Assert.Equal("custom", config.ServerLabel);
    }

    [Fact]
    public void SavedServersSurviveARoundTripThroughTheConfigFile()
    {
        var json = """
        {
          "baseUrl": "http://10.0.0.5:9656",
          "server": "work",
          "servers": [
            { "name": "work", "url": "http://10.0.0.5:9656" },
            { "name": "home", "url": "http://127.0.0.1:9657" }
          ]
        }
        """;

        var config = System.Text.Json.JsonSerializer.Deserialize<ClientConfig>(json)!;

        Assert.Equal("work", config.ServerName);
        Assert.Equal(2, config.Servers.Count);
        Assert.Equal("home", config.Servers[1].Name);
        Assert.Equal("http://127.0.0.1:9657", config.Servers[1].Url);
    }

    /// <summary>A config written before servers existed must still load cleanly.</summary>
    [Fact]
    public void AnOlderConfigWithoutServersLoadsWithAnEmptyList()
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<ClientConfig>(
            """{"baseUrl":"http://127.0.0.1:9656","model":"deepseek-v4-pro"}""")!;

        Assert.Empty(config.Servers);
        Assert.Equal("custom", config.ServerLabel);
    }
}