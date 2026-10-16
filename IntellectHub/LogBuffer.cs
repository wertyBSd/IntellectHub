namespace IntellectHub;

internal sealed record LogEntry(LogLevel Level, DateTime At, string Message);

/// <summary>Which levels are shown, and an optional substring the line must contain.</summary>
internal sealed class LogFilter
{
    public bool ShowInfo { get; set; } = true;
    public bool ShowOk { get; set; } = true;
    public bool ShowWarn { get; set; } = true;
    public bool ShowError { get; set; } = true;

    /// <summary>Verbose chatter, off by default: it drowns out everything else.</summary>
    public bool ShowMuted { get; set; }

    public string Search { get; set; } = "";

    public bool IsDefault =>
        ShowInfo && ShowOk && ShowWarn && ShowError && !ShowMuted && Search.Length == 0;

    public bool Matches(LogEntry entry)
    {
        if (!LevelVisible(entry.Level)) return false;

        if (Search.Length > 0 &&
            !entry.Message.Contains(Search, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public bool LevelVisible(LogLevel level) => level switch
    {
        LogLevel.Ok => ShowOk,
        LogLevel.Warn => ShowWarn,
        LogLevel.Error => ShowError,
        LogLevel.Muted => ShowMuted,
        _ => ShowInfo,
    };
}

/// <summary>
/// The in-memory console history.
///
/// The buffer owns filtering and trimming so the form only ever renders a view of
/// it. Trimming a List is free; trimming a RichTextBox means re-scanning the whole
/// document, which is what made the console crawl once log volume grew.
/// </summary>
internal sealed class LogBuffer
{
    private readonly List<LogEntry> _entries = new();

    public LogBuffer(int capacity = 2000) => Capacity = capacity;

    public int Capacity { get; }

    public int Count => _entries.Count;

    /// <summary>Lines pushed out by trimming, so the UI can say "N скрыто".</summary>
    public int Dropped { get; private set; }

    public void Add(LogLevel level, DateTime at, string message)
    {
        _entries.Add(new LogEntry(level, at, message));

        if (_entries.Count > Capacity)
        {
            var excess = _entries.Count - Capacity;
            _entries.RemoveRange(0, excess);
            Dropped += excess;
        }
    }

    /// <summary>
    /// The lines a filter lets through, oldest first.
    ///
    /// There is deliberately no "filter is wide open" shortcut: the default filter
    /// hides the verbose tier, so it is not a pass-through and skipping the check
    /// would leak exactly the noise the default is there to hide.
    /// </summary>
    public List<LogEntry> View(LogFilter filter)
    {
        var view = new List<LogEntry>();
        foreach (var entry in _entries)
            if (filter.Matches(entry))
                view.Add(entry);

        return view;
    }

    public void Clear()
    {
        _entries.Clear();
        Dropped = 0;
    }

    public static string Format(LogEntry entry) =>
        $"{entry.At:HH:mm:ss} [{Prefix(entry.Level)}] {entry.Message}";

    public static string Prefix(LogLevel level) => level switch
    {
        LogLevel.Ok => "  ok ",
        LogLevel.Warn => "warn ",
        LogLevel.Error => " err ",
        LogLevel.Muted => "     ",
        _ => " info",
    };
}