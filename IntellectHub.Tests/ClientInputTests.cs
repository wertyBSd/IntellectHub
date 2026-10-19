using IntellectHub.Client;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// The input grammar and the settings validators.
///
/// A slash that names nothing must never fall through to the model: the user pays
/// for the request and gets an answer to a question they did not ask, with no
/// hint that the shell is what went wrong. That failure is exactly what a typo
/// produces, so the parser is pinned here.
/// </summary>
public sealed class CommandLineTests
{
    [Theory]
    [InlineData("/help", "/help")]
    [InlineData("/HELP", "/help")]
    [InlineData("/h", "/help")]
    [InlineData("/?", "/help")]
    [InlineData("/quit", "/exit")]
    [InlineData("/q", "/exit")]
    [InlineData("/cfg", "/config")]
    [InlineData("/settings", "/config")]
    [InlineData("/cd", "/project")]
    [InlineData("/reset", "/clear")]
    [InlineData("/server", "/connect")]
    [InlineData("/url", "/connect")]
    public void NamesAndAliasesResolve(string input, string expected)
    {
        var parsed = CommandLine.Parse(input);

        Assert.Equal(InputKind.Command, parsed.Kind);
        Assert.Equal(expected, parsed.Command!.Name);
    }

    [Fact]
    public void ArgumentsAreSplitOff()
    {
        var parsed = CommandLine.Parse("/config server http://127.0.0.1:9657");

        Assert.Equal(InputKind.Command, parsed.Kind);
        Assert.Equal("server http://127.0.0.1:9657", parsed.Arguments);
    }

    [Fact]
    public void ExtraSpacingInArgumentsIsTolerated()
    {
        Assert.Equal("server   http://x", CommandLine.Parse("/config   server   http://x").Arguments);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("see /config for details")]
    [InlineData("/")]
    [InlineData("  ")]
    [InlineData("a / b")]
    public void OrdinaryTextStaysChat(string input)
    {
        // A bare slash, or one inside a sentence, is not a command.
        Assert.Equal(InputKind.Chat, CommandLine.Parse(input).Kind);
    }

    /// <summary>
    /// The strictness is deliberate and is what makes typos recoverable: anything
    /// that opens with a slash is claimed by the shell. A filesystem path typed at
    /// the prompt is the cost, and it is a far rarer thing to mean here than a
    /// mistyped command is.
    /// </summary>
    [Theory]
    [InlineData("/v1/models is the path")]
    [InlineData("/usr/local/bin")]
    public void ASlashAlwaysClaimsTheLine(string input) =>
        Assert.Equal(InputKind.UnknownCommand, CommandLine.Parse(input).Kind);

    [Theory]
    [InlineData("/consfig", "/config")]
    [InlineData("/cofnig", "/config")]
    [InlineData("/clera", "/clear")]
    [InlineData("/hepl", "/help")]
    public void AMistypedCommandIsReportedWithASuggestion(string input, string expected)
    {
        var parsed = CommandLine.Parse(input);

        Assert.Equal(InputKind.UnknownCommand, parsed.Kind);
        Assert.Equal("/" + input[1..], parsed.Token);
        Assert.Equal(expected, parsed.Suggestion);
    }

    [Fact]
    public void AWildlyDifferentCommandGetsNoSuggestion()
    {
        // Offering a correction for something this far off would be noise, and the
        // command list is one keypress away anyway.
        Assert.Null(CommandLine.Parse("/zzzzzzz").Suggestion);
    }

    [Fact]
    public void EveryCommandNameIsLowercaseAndStartsWithSlash()
    {
        foreach (var command in CommandLine.Known)
        {
            foreach (var name in command.AllNames)
            {
                Assert.StartsWith("/", name);
                Assert.Equal(name.ToLowerInvariant(), name);
                Assert.Equal(name, name.Trim());
            }
        }
    }

    /// <summary>
    /// The sidebar and the help text are rendered from the table, so a command the
    /// parser accepts can never be one the user is never told about.
    /// </summary>
    [Fact]
    public void TheHelpTextCoversEveryCommand()
    {
        foreach (var command in CommandLine.Known)
        {
            Assert.True(CommandLine.Parse(command.Name).Kind == InputKind.Command);
            Assert.False(string.IsNullOrWhiteSpace(command.Usage));
            Assert.False(string.IsNullOrWhiteSpace(command.Summary));
        }
    }
}

public sealed class SettingTests
{
    private static string? Validate(string name, string value) =>
        Setting.Find(name)?.Validate(value);

    [Theory]
    [InlineData("http://127.0.0.1:9657")]
    [InlineData("https://gateway.example.com")]
    public void AValidServerUrlPasses(string url) => Assert.Null(Validate("server", url));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://host/path")]
    [InlineData("http://")]
    public void AnUnusableServerUrlIsRejectedWithAReason(string url) =>
        Assert.False(string.IsNullOrWhiteSpace(Validate("server", url)));

    [Fact]
    public void BareHostAndPortIsAcceptedAndMadeAbsolute()
    {
        // The most likely thing to type, and it used to be stored verbatim and fail.
        Assert.Null(Validate("server", "127.0.0.1:9657"));
        Assert.Equal("http://127.0.0.1:9657", Setting.NormalizeBaseUrl("127.0.0.1:9657"));
        Assert.Equal("http://127.0.0.1:9657", Setting.NormalizeBaseUrl("http://127.0.0.1:9657/"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("many")]
    public void BadMaxTokensIsRejected(string value) =>
        Assert.False(string.IsNullOrWhiteSpace(Validate("maxtokens", value)));

    [Theory]
    [InlineData("3")]
    [InlineData("-1")]
    [InlineData("warm")]
    public void BadTemperatureIsRejected(string value) =>
        Assert.False(string.IsNullOrWhiteSpace(Validate("temperature", value)));

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("0.7")]
    public void ReasonableTemperaturePasses(string value) => Assert.Null(Validate("temperature", value));

    [Theory]
    [InlineData("server", "url")]
    [InlineData("maxtokens", "tokens")]
    [InlineData("temperature", "temp")]
    public void SettingsResolveByNameAndAlias(string name, string alias)
    {
        Assert.NotNull(Setting.Find(name));
        Assert.Same(Setting.Find(name), Setting.Find(alias));
    }

    [Fact]
    public void AMistypedSettingSuggestsTheRealOne()
    {
        Assert.Equal("maxtokens", Setting.Suggest("maxtoken"));
        Assert.Equal("temperature", Setting.Suggest("temprature"));
        Assert.Null(Setting.Suggest("zzzzzzzz"));
    }

    [Fact]
    public void TheApiKeyIsNeverShownInFull()
    {
        var config = new ClientConfig { ApiKey = "ih_0123456789abcdef" };
        var setting = Setting.Find("key")!;

        var shown = setting.Display(config);

        Assert.DoesNotContain("0123456789", shown);
        Assert.EndsWith("cdef", shown);
    }

    [Fact]
    public void AnAbsentApiKeyDisplaysAsNone()
    {
        Assert.Equal("(none)", Setting.Find("key")!.Display(new ClientConfig { ApiKey = null }));
    }

    [Fact]
    public void EverySettingValidatesItsOwnRoundTrip()
    {
        // Guards against a setting whose Write produces something its own Validate
        // would reject, which would leave the config in a state it cannot be set to.
        var config = new ClientConfig();

        foreach (var setting in Setting.All.Where(s => s.Name is not ("key" or "system")))
        {
            var sample = setting.Name switch
            {
                "server" => "http://127.0.0.1:9657",
                "model" => "deepseek-v4-flash",
                _ => setting.Name == "maxtokens" ? "4096" : "0.7",
            };

            setting.Write(config, sample);
            Assert.Null(setting.Validate(setting.Read(config)));
        }
    }

    /// <summary>
    /// Each setting carries its own default. When the defaults lived in a switch
    /// keyed by name, a casing slip between the two lists silently reset one
    /// setting to another's value.
    /// </summary>
    [Fact]
    public void ResettingASettingRestoresItsOwnDefault()
    {
        var defaults = new ClientConfig();

        foreach (var setting in Setting.All)
        {
            // Dirty every setting first, so a reset has something to undo.
            setting.Write(defaults, setting.Name switch
            {
                "server" => "http://elsewhere.invalid:1234",
                "model" => "something-else",
                "key" => "ih_something",
                "system" => "other instructions",
                "maxtokens" => "17",
                _ => "1.9",
            });

            setting.Write(defaults, setting.DefaultValue);

            Assert.Equal(setting.DefaultValue, setting.Read(defaults));
        }
    }

    [Fact]
    public void TheDefaultsAreDistinctPerSetting()
    {
        // Catches a copy-paste that gives two settings the same fallback value.
        var numeric = Setting.All
            .Where(s => s.Name is "maxtokens" or "temperature")
            .Select(s => s.DefaultValue)
            .ToList();

        Assert.Equal(numeric.Count, numeric.Distinct().Count());
        Assert.Equal("4096", numeric[0]);
        Assert.Equal("1", numeric[1]);
    }
}