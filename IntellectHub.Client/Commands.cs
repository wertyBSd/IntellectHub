namespace IntellectHub.Client;

/// <summary>One internal command, as the user types it.</summary>
internal sealed record Command(
    string Name,
    string Usage,
    string Summary,
    string[]? Aliases = null,
    bool Hidden = false)
{
    private static readonly string[] NoAliases = [];

    public bool Matches(string token) =>
        string.Equals(Name, token, StringComparison.OrdinalIgnoreCase) ||
        (Aliases ?? NoAliases).Any(alias => string.Equals(alias, token, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<string> AllNames
    {
        get
        {
            yield return Name;
            foreach (var alias in Aliases ?? NoAliases) yield return alias;
        }
    }
}

/// <summary>What a line typed at the prompt actually was.</summary>
internal enum InputKind
{
    /// <summary>Ordinary text to send to the model.</summary>
    Chat,

    /// <summary>A recognised internal command.</summary>
    Command,

    /// <summary>Started with a slash but names nothing.</summary>
    UnknownCommand,
}

internal sealed record ParsedInput
{
    public required InputKind Kind { get; init; }
    public required string Raw { get; init; }
    public Command? Command { get; init; }
    public string Arguments { get; init; } = "";

    /// <summary>The slash token as typed, for an unknown command.</summary>
    public string Token { get; init; } = "";

    /// <summary>Closest known command to <see cref="Token"/>, when one is close enough to be worth offering.</summary>
    public string? Suggestion { get; init; }

    public static ParsedInput AsChat(string raw) => new() { Kind = InputKind.Chat, Raw = raw };
}

/// <summary>
/// Splits a line of input into an internal command or chat text.
///
/// Anything starting with a slash is treated as a command. That is the whole point:
/// a mistyped <c>/consfig</c> must be reported as a mistyped command, never sent to
/// the model as a question. The user otherwise gets a model reply, a token charge
/// and no idea that the shell is what went wrong.
/// </summary>
internal static class CommandLine
{
    /// <summary>
    /// A candidate has to be within this share of the typed length to be offered as
    /// a correction. Loose enough to catch a transposition, tight enough that
    /// <c>/models</c> does not get read as a typo of <c>/project</c>.
    /// </summary>
    private const int MaxSuggestDistance = 3;

    public static IReadOnlyList<Command> Known { get; } =
    [
        new("/help",     "/help",              "show this help", ["/h", "/?"]),
        new("/models",   "/models",            "choose a model from the server's list"),
        new("/config",   "/config [name] [value]", "settings menu; edit without typing a value", ["/cfg", "/settings"]),
        new("/connect",  "/connect [name|url|key|save|forget|add]", "switch provider; name a preset, save a URL or store the provider's key", ["/server", "/url", "/key"]),
        new("/approve",  "/approve [ask|writes|all]", "what runs without asking; Shift+Tab cycles", ["/approvals"]),
        new("/project",  "/project [path]",    "switch workspace from recent folders", ["/cd"]),
        new("/clear",    "/clear",             "clear the conversation", ["/reset"]),
        new("/plan",     "/plan",              "break current task into subtasks", ["/decompose"]),
        new("/mode",     "/mode [architect|code|debug|orchestrator|next|reset|status]", "переключить режим разработки и показать состояние модов", ["/m"]),
        new("/preview",  "/preview [path]", "открыть проект в браузере через локальный сервер и проверить JS-ошибки", ["/view", "/browser"]),
        new("/exit",     "/exit",              "leave", ["/quit", "/q"]),
    ];

    public static ParsedInput Parse(string input)
    {
        var raw = input.Trim();
        if (!raw.StartsWith('/')) return ParsedInput.AsChat(raw);

        var token = raw[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        var arguments = raw[(token.Length + 1)..].Trim();
        if (token.Length == 0)
        {
            // A bare slash: a chat message that happens to start with one, not a typo.
            return ParsedInput.AsChat(raw);
        }

        var command = Known.FirstOrDefault(c => c.Matches("/" + token));
        if (command is not null)
            return new ParsedInput
            {
                Kind = InputKind.Command,
                Raw = raw,
                Command = command,
                Arguments = arguments,
            };

        return new ParsedInput
        {
            Kind = InputKind.UnknownCommand,
            Raw = raw,
            Token = "/" + token,
            Suggestion = Suggest(token),
        };
    }

    /// <summary>Nearest known command name to <paramref name="token"/>, or null.</summary>
    public static string? Suggest(string token)
    {
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var name in Known.SelectMany(c => c.AllNames))
        {
            var distance = Distance(token, name[1..]);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = name;
        }

        var limit = Math.Min(MaxSuggestDistance, Math.Max(1, token.Length / 2));
        return bestDistance <= limit ? best : null;
    }

    /// <summary>Levenshtein distance, early-exited once it can no longer win.</summary>
    private static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowBest = current[0];

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowBest = Math.Min(rowBest, current[j]);
            }

            if (rowBest > MaxSuggestDistance) return rowBest;

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}