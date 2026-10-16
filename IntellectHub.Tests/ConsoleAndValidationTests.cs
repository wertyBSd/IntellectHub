using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Stage 4: the console's filter and search, and the settings form's validation.
/// Both are pure logic, so both are checked without a window.
/// </summary>
public sealed class ConsoleAndValidationTests
{
    private static LogEntry Entry(LogLevel level, string message) => new(level, DateTime.Now, message);

    // ---- level filtering ---------------------------------------------------------

    [Fact]
    public void ByDefaultErrorsWarningsAndInfoAreShownButNotNoise()
    {
        var filter = new LogFilter();

        Assert.True(filter.Matches(Entry(LogLevel.Error, "boom")));
        Assert.True(filter.Matches(Entry(LogLevel.Warn, "hmm")));
        Assert.True(filter.Matches(Entry(LogLevel.Ok, "fine")));
        Assert.True(filter.Matches(Entry(LogLevel.Info, "note")));

        // The verbose tier drowns out everything else, so it stays off.
        Assert.False(filter.Matches(Entry(LogLevel.Muted, "chatter")));
    }

    [Fact]
    public void TurningOffALevelHidesOnlyThatLevel()
    {
        var filter = new LogFilter { ShowInfo = false };

        Assert.False(filter.Matches(Entry(LogLevel.Info, "note")));
        Assert.True(filter.Matches(Entry(LogLevel.Error, "boom")));
        Assert.True(filter.Matches(Entry(LogLevel.Warn, "hmm")));
    }

    [Fact]
    public void AnErrorsOnlyFilterIsEasyToRead()
    {
        var filter = new LogFilter
        {
            ShowInfo = false, ShowOk = false, ShowWarn = false, ShowError = true,
        };

        Assert.True(filter.Matches(Entry(LogLevel.Error, "boom")));
        Assert.False(filter.Matches(Entry(LogLevel.Warn, "hmm")));
        Assert.False(filter.Matches(Entry(LogLevel.Info, "note")));
    }

    // ---- search ------------------------------------------------------------------

    [Fact]
    public void SearchNarrowsTheViewAndIsCaseInsensitive()
    {
        var filter = new LogFilter { Search = "троттлинг" };

        Assert.True(filter.Matches(Entry(LogLevel.Warn, "аккаунт X троттлинг")));
        Assert.False(filter.Matches(Entry(LogLevel.Info, "аккаунт X готов")));
    }

    [Fact]
    public void SearchAndLevelFilterCombine()
    {
        var filter = new LogFilter { Search = "ошибка", ShowWarn = false };

        Assert.True(filter.Matches(Entry(LogLevel.Error, "ошибка чтения")));
        Assert.False(filter.Matches(Entry(LogLevel.Warn, "ошибка записи")));
    }

    [Fact]
    public void ADefaultFilterNeedsNoRescan()
    {
        var filter = new LogFilter();

        Assert.True(filter.IsDefault);

        filter.Search = "x";
        Assert.False(filter.IsDefault);

        var quiet = new LogFilter { ShowMuted = true };
        Assert.False(quiet.IsDefault);
    }

    // ---- buffer ------------------------------------------------------------------

    [Fact]
    public void TheBufferTrimsOldestLinesAndCountsThem()
    {
        var buffer = new LogBuffer(capacity: 10);
        for (var i = 0; i < 25; i++) buffer.Add(LogLevel.Info, DateTime.Now, $"строка {i}");

        Assert.Equal(10, buffer.Count);
        Assert.Equal(15, buffer.Dropped);

        // The survivors are the newest ones, which is what you actually want.
        var view = buffer.View(new LogFilter());
        Assert.Contains("строка 24", view[^1].Message);
        Assert.DoesNotContain("строка 0", string.Join("\n", view.Select(v => v.Message)));
    }

    [Fact]
    public void TheViewHonoursTheFilter()
    {
        var buffer = new LogBuffer(capacity: 100);
        buffer.Add(LogLevel.Info, DateTime.Now, "обычное");
        buffer.Add(LogLevel.Error, DateTime.Now, "поломка");
        buffer.Add(LogLevel.Muted, DateTime.Now, "шум");

        var view = buffer.View(new LogFilter());
        Assert.Equal(2, view.Count);

        var errorsOnly = buffer.View(new LogFilter { ShowInfo = false, ShowOk = false, ShowWarn = false });
        Assert.Equal("поломка", Assert.Single(errorsOnly).Message);
    }

    [Fact]
    public void FilteringPicksUpLinesThatArrivedEarlier()
    {
        var buffer = new LogBuffer(capacity: 100);
        buffer.Add(LogLevel.Info, DateTime.Now, "первая");
        buffer.Add(LogLevel.Error, DateTime.Now, "вторая");

        // This is the case a naive append-only view gets wrong: the filter changes
        // after the line was already written, so the view has to be rebuilt.
        var view = buffer.View(new LogFilter { ShowInfo = false, ShowOk = false, ShowWarn = false });
        Assert.Equal("вторая", Assert.Single(view).Message);
    }

    [Fact]
    public void ClearingEmptiesTheBufferAndTheCounter()
    {
        var buffer = new LogBuffer(capacity: 5);
        for (var i = 0; i < 20; i++) buffer.Add(LogLevel.Info, DateTime.Now, "x");

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.Dropped);
    }

    [Fact]
    public void FormattedLinesLookLikeTheConsole()
    {
        var line = LogBuffer.Format(new LogEntry(LogLevel.Warn, new DateTime(2026, 1, 1, 14, 30, 5), "троттлинг"));

        Assert.Contains("14:30:05", line);
        Assert.Contains("warn", line);
        Assert.Contains("троттлинг", line);
    }

    // ---- validation ---------------------------------------------------------------

    // Deliberately not normalised: the validator runs on what the user typed, before
    // Normalize silently repairs it. Validating afterwards could never fail.
    private static AppConfig Valid() => new();

    [Fact]
    public void AFreshConfigIsValid()
    {
        Assert.Empty(SettingsValidator.Validate(Valid()));
    }

    [Fact]
    public void AnImpossiblePortIsRejected()
    {
        var problems = SettingsValidator.Validate(new AppConfig { Port = 70000 });
        Assert.Contains(problems, p => p.Field == nameof(AppConfig.Port));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.10")]
    [InlineData("gateway.local")]
    public void SensibleHostsAreAccepted(string host)
    {
        var config = Valid();
        config.Host = host;

        Assert.DoesNotContain(SettingsValidator.Validate(config), p => p.Field == nameof(AppConfig.Host));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("a..b")]
    public void BadHostsAreRejected(string host)
    {
        var config = Valid();
        config.Host = host;

        Assert.Contains(SettingsValidator.Validate(config), p => p.Field == nameof(AppConfig.Host));
    }

    [Fact]
    public void ADeadlineShorterThanTheUpstreamTimeoutIsRejected()
    {
        var config = Valid();
        config.UpstreamTimeoutSeconds = 120;
        config.RequestDeadlineSeconds = 60;

        // A request would be cut off before its own attempt could ever finish.
        Assert.Contains(SettingsValidator.Validate(config), p => p.Field == nameof(AppConfig.RequestDeadlineSeconds));
    }

    [Fact]
    public void WaitingLongerThanTheDeadlineIsRejected()
    {
        var config = Valid();
        config.RequestDeadlineSeconds = 60;
        config.LockWaitSeconds = 120;

        Assert.Contains(SettingsValidator.Validate(config), p => p.Field == nameof(AppConfig.LockWaitSeconds));
    }

    [Fact]
    public void ATooSmallPromptLimitIsRejected()
    {
        var config = Valid();
        config.MaxPromptChars = 100;

        Assert.Contains(SettingsValidator.Validate(config), p => p.Field == nameof(AppConfig.MaxPromptChars));
    }

    [Fact]
    public void EveryProblemNamesItsFieldSoTheWindowCanPointAtIt()
    {
        var config = Valid();
        config.Host = "bad host";
        config.Port = 0;
        config.MaxConcurrent = 9999;

        var problems = SettingsValidator.Validate(config);

        Assert.True(problems.Count >= 3);
        Assert.All(problems, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Field));
            Assert.False(string.IsNullOrWhiteSpace(p.Message));
        });
        Assert.Contains(problems, p => p.Field == nameof(AppConfig.Host));
        Assert.Contains(problems, p => p.Field == nameof(AppConfig.Port));
        Assert.Contains(problems, p => p.Field == nameof(AppConfig.MaxConcurrent));
    }
}