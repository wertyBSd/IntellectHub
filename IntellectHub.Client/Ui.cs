using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;
using Color = Spectre.Console.Color;
using SpectrePanel = Spectre.Console.Panel;
using SpectrePadding = Spectre.Console.Padding;

namespace IntellectHub.Client;

public enum BarrierChoice
{
    Run,
    Step,
    Edit,
    Quit,
}

/// <summary>How a transcript line is drawn. The layout owns the markers, callers only say what a line is.</summary>
public enum EntryStyle
{
    Plain,
    User,
    Assistant,
    Tool,
    ToolOutput,
    Error,
    Notice,
    Thought,
}

/// <summary>
/// What the prompt handed back. Almost every shortcut is translated into the
/// slash command it stands for, so a key and the typed command cannot drift
/// apart. The exceptions are the few that are not commands of their own.
/// </summary>
public enum PromptAction
{
    None,
    Exit,
    CycleApprovals,
}

public readonly record struct PromptResult(string Text, PromptAction Action = PromptAction.None)
{
    public static readonly PromptResult Exit = new("", PromptAction.Exit);
}

/// <summary>One row of an interactive picker.</summary>
public sealed record PickItem(string Label, string? Detail = null, string? Badge = null, bool Active = false);

public sealed class Ui : IDisposable
{
    private static readonly string[] Frames = { "·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢" };

    private const int HeaderHeight = 3;
    private const int FooterHeight = 5;
    private const int SidebarWidth = 30;
    private const int TranscriptLimit = 2000;

    private readonly object _gate = new();
    private readonly List<Entry> _transcript = new();

    // Recalled input is kept per prompt. One shared list meant that arrowing up
    // inside /config offered previous chat messages, which is never what the
    // person editing a setting wants to see.
    private readonly Dictionary<string, List<string>> _historyByPrompt = new();
    private readonly List<string> _tools = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _interactive;
    private readonly bool _controlCAsInput;

    private IRenderable? _header;
    private IRenderable? _overlay;
    private string _overlayHint = "";
    private Layout? _layout;
    private bool _layoutSidebar;
    private Task? _render;

    private string _prompt = "";
    private string _input = "";
    private int _cursor;
    private string _status = "ready";
    private DateTime _busySince = DateTime.UtcNow;
    private int _frame;
    private int _histCursor;
    private int _scrollFromBottom;
    private int _liveIndex = -1;
    private bool _busy;
    private bool _disposed;

    private bool _sidebar = true;
    private bool _toolDetails = true;
    private int _messages;
    private int _approxTokens;

    private string _model = "";
    private string _server = "";
    private string _project = "";
    private string _approvals = "";

    private sealed record Entry(string Text, Color Color, EntryStyle Style, int Indent, bool Head);

    public Ui()
    {
        EnableVirtualTerminal();
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        _interactive = AnsiConsole.Profile.Capabilities.Interactive;
        if (_interactive)
        {
            // Without this the shell kills the process on Ctrl+C before ReadKey
            // sees it, and the prompt's "clear the line first" behaviour is dead code.
            try
            {
                Console.TreatControlCAsInput = true;
                _controlCAsInput = true;
            }
            catch { }

            _render = Task.Run(() => RenderLoop(_cts.Token));
        }
    }

    public bool IsInteractive => _interactive;

    public void SetHeader(IRenderable header)
    {
        lock (_gate) _header = header;
    }

    public void SetTools(IEnumerable<string> tools)
    {
        lock (_gate)
        {
            _tools.Clear();
            _tools.AddRange(tools);
        }
    }

    /// <summary>What the rail says about the session; kept apart from the header so it stays cheap to refresh.</summary>
    public void SetContext(string model, string server, string project, string approvals)
    {
        lock (_gate)
        {
            _model = model;
            _server = server;
            _project = project;
            _approvals = approvals;
        }
    }

    public void SetUsage(int messages, int approxTokens)
    {
        lock (_gate)
        {
            _messages = messages;
            _approxTokens = approxTokens;
        }
    }

    public void Write(string text, ConsoleColor color = ConsoleColor.Gray, EntryStyle style = EntryStyle.Plain)
    {
        var tint = ToColor(color);
        var indent = IndentFor(style);
        lock (_gate)
        {
            var head = true;
            foreach (var line in Split(text))
            {
                _transcript.Add(new Entry(line, tint, style, indent, head));
                head = false;
            }
            if (_transcript.Count > TranscriptLimit)
                _transcript.RemoveRange(0, _transcript.Count - TranscriptLimit);
            _scrollFromBottom = 0;
        }

        if (!_interactive)
            Console.WriteLine(text);
    }

    public void BeginStream(ConsoleColor color = ConsoleColor.White, EntryStyle style = EntryStyle.Assistant)
    {
        lock (_gate)
        {
            _transcript.Add(new Entry("", ToColor(color), style, IndentFor(style), true));
            _liveIndex = _transcript.Count - 1;
            _scrollFromBottom = 0;
        }
    }

    public void PushStream(string delta)
    {
        if (delta.Length == 0) return;
        lock (_gate)
        {
            if (_liveIndex < 0 || _liveIndex >= _transcript.Count) return;
            var entry = _transcript[_liveIndex];
            _transcript[_liveIndex] = entry with { Text = entry.Text + delta };
            _scrollFromBottom = 0;
        }
        if (!_interactive) Console.Write(delta);
    }

    public void EndStream()
    {
        lock (_gate) _liveIndex = -1;
        if (!_interactive) Console.WriteLine();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _transcript.Clear();
            _liveIndex = -1;
            _scrollFromBottom = 0;
        }
        if (!_interactive)
        {
            try { Console.Clear(); } catch { }
        }
    }

    public void Busy(string status)
    {
        lock (_gate)
        {
            _status = status;
            _busy = true;
            _busySince = DateTime.UtcNow;
            _frame = 0;
        }
    }

    public void Idle()
    {
        lock (_gate)
        {
            _busy = false;
            _status = "ready";
        }
    }

    /// <summary>
    /// Reads one line, or the command a shortcut stands for. <paramref name="initial"/>
    /// pre-fills the line so changing a setting starts from its current value.
    /// </summary>
    public PromptResult ReadLine(string? prompt = null, string initial = "")
    {
        if (!_interactive)
        {
            if (prompt is not null) Console.Write(prompt + " ");
            var line = Console.ReadLine();
            return line is null ? PromptResult.Exit : new PromptResult(line);
        }

        lock (_gate)
        {
            _prompt = prompt ?? "";
            _input = initial ?? "";
            _cursor = _input.Length;
            _histCursor = HistoryFor(_prompt).Count;
            _scrollFromBottom = 0;
        }

        while (true)
        {
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(intercept: true); }
            catch (InvalidOperationException) { return PromptResult.Exit; }

            var ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
            var shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);

            // The command shortcuts belong to the chat prompt. In a sub-prompt
            // ("model > ", "path > ") they would type a command into a setting.
            if (prompt is null)
            {
                switch (key.Key)
                {
                    case ConsoleKey.K when ctrl:
                    {
                        var chosen = Palette();
                        if (chosen is not null) return new PromptResult(chosen);
                        continue;
                    }

                    case ConsoleKey.O when ctrl: return new PromptResult("/config");
                    case ConsoleKey.L when ctrl: return new PromptResult("/clear");
                    case ConsoleKey.G when ctrl: return new PromptResult("/connect");
                    case ConsoleKey.P when ctrl: return new PromptResult("/project");
                    case ConsoleKey.B when ctrl: ToggleSidebar(); continue;
                    case ConsoleKey.T when ctrl: ToggleToolDetails(); continue;

                    case ConsoleKey.F1: return new PromptResult("/help");
                    case ConsoleKey.F2: return new PromptResult("/config");
                    case ConsoleKey.F3: return new PromptResult("/models");
                    case ConsoleKey.F4: return new PromptResult("/connect");
                    case ConsoleKey.F5: return new PromptResult("/project");
                }

                if (key.Key == ConsoleKey.Tab)
                {
                    if (shift) return new PromptResult("", PromptAction.CycleApprovals);
                    Complete();
                    continue;
                }
            }

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                {
                    string text;
                    lock (_gate)
                    {
                        text = _input.Trim();
                        var history = HistoryFor(_prompt);
                        if (text.Length > 0 && (history.Count == 0 || history[^1] != text))
                            history.Add(text);
                        _histCursor = history.Count;
                        _input = "";
                        _cursor = 0;
                    }
                    return new PromptResult(text);
                }

                case ConsoleKey.Backspace:
                    lock (_gate)
                    {
                        if (_cursor > 0)
                        {
                            _input = _input.Remove(_cursor - 1, 1);
                            _cursor--;
                        }
                    }
                    break;

                case ConsoleKey.Delete:
                    lock (_gate)
                    {
                        if (_cursor < _input.Length) _input = _input.Remove(_cursor, 1);
                    }
                    break;

                case ConsoleKey.LeftArrow:
                    if (ctrl) MoveCursor(WordLeft(_input, _cursor));
                    else MoveCursor(_cursor - 1);
                    break;

                case ConsoleKey.RightArrow:
                    if (ctrl) MoveCursor(WordRight(_input, _cursor));
                    else MoveCursor(_cursor + 1);
                    break;

                case ConsoleKey.UpArrow:
                    Navigate(-1);
                    break;

                case ConsoleKey.DownArrow:
                    Navigate(1);
                    break;

                case ConsoleKey.PageUp:
                    Scroll(PageSize());
                    break;

                case ConsoleKey.PageDown:
                    Scroll(-PageSize());
                    break;

                case ConsoleKey.Home:
                    if (ctrl) Scroll(int.MaxValue);
                    else MoveCursor(0);
                    break;

                case ConsoleKey.End:
                    if (ctrl) Scroll(int.MinValue);
                    else MoveCursor(_input.Length);
                    break;

                case ConsoleKey.Escape:
                    lock (_gate)
                    {
                        _input = "";
                        _cursor = 0;
                    }
                    break;

                case ConsoleKey.C when ctrl:
                    // Ctrl+C empties a half-typed line first, then exits on the next
                    // press: the same "cancel, do not lose work" habit as a shell.
                    lock (_gate)
                    {
                        if (_input.Length > 0)
                        {
                            _input = "";
                            _cursor = 0;
                            break;
                        }
                    }
                    return PromptResult.Exit;

                case ConsoleKey.Q when ctrl:
                    return PromptResult.Exit;

                case ConsoleKey.A when ctrl: MoveCursor(0); break;
                case ConsoleKey.E when ctrl: MoveCursor(_input.Length); break;
                case ConsoleKey.U when ctrl:
                    lock (_gate)
                    {
                        _input = _input[Math.Clamp(_cursor, 0, _input.Length)..];
                        _cursor = 0;
                    }
                    break;

                case ConsoleKey.W when ctrl:
                    lock (_gate)
                    {
                        var from = WordLeft(_input, _cursor);
                        _input = _input.Remove(from, Math.Max(0, _cursor - from));
                        _cursor = from;
                    }
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                        lock (_gate)
                        {
                            _cursor = Math.Clamp(_cursor, 0, _input.Length);
                            _input = _input.Insert(_cursor, key.KeyChar.ToString());
                            _cursor++;
                        }
                    break;
            }
        }
    }

    /// <summary>
    /// A searchable list. Returns the index in <paramref name="items"/>, or null when
    /// the person backed out. Blocks while open, exactly like ReadLine.
    /// </summary>
    public int? Pick(string title, IReadOnlyList<PickItem> items, string hint, int selected = 0, bool filter = true)
    {
        if (!_interactive || items.Count == 0) return null;

        var query = "";
        var position = Math.Clamp(selected, 0, items.Count - 1);

        try
        {
            while (true)
            {
                var matches = Match(items, query);
                position = matches.Count == 0 ? 0 : Math.Clamp(position, 0, matches.Count - 1);

                lock (_gate)
                {
                    _overlay = BuildPicker(title, matches, position, query, filter);
                    _overlayHint = hint;
                }

                ConsoleKeyInfo key;
                try { key = Console.ReadKey(intercept: true); }
                catch (InvalidOperationException) { return null; }

                var ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);

                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        return null;

                    case ConsoleKey.C when ctrl:
                        return null;

                    case ConsoleKey.Enter:
                        if (matches.Count > 0) return matches[position].Index;
                        continue;

                    case ConsoleKey.UpArrow:
                        if (matches.Count > 0) position = (position - 1 + matches.Count) % matches.Count;
                        continue;

                    case ConsoleKey.DownArrow:
                        if (matches.Count > 0) position = (position + 1) % matches.Count;
                        continue;

                    case ConsoleKey.Home:
                        position = 0;
                        continue;

                    case ConsoleKey.End:
                        position = Math.Max(0, matches.Count - 1);
                        continue;

                    case ConsoleKey.PageUp:
                        position = Math.Max(0, position - 8);
                        continue;

                    case ConsoleKey.PageDown:
                        position = Math.Min(Math.Max(0, matches.Count - 1), position + 8);
                        continue;

                    case ConsoleKey.Backspace:
                        if (filter && query.Length > 0)
                        {
                            query = query[..^1];
                            position = 0;
                        }
                        continue;
                }

                if (filter && !char.IsControl(key.KeyChar))
                {
                    query += key.KeyChar;
                    position = 0;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _overlay = null;
                _overlayHint = "";
            }
        }
    }

    private string? Palette()
    {
        var items = CommandLine.Known
            .Where(c => !c.Hidden)
            .Select(c => new PickItem(c.Name, c.Summary, c.Usage))
            .ToList();

        var chosen = Pick("commands", items, "type to filter · ↑↓ move · ⏎ run · Esc close");
        return chosen is null ? null : items[chosen.Value].Label;
    }

    private static List<(int Index, PickItem Item)> Match(IReadOnlyList<PickItem> items, string query)
    {
        var matches = new List<(int, PickItem)>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (query.Length == 0 ||
                item.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (item.Detail?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                matches.Add((i, item));
        }
        return matches;
    }

    private IRenderable BuildPicker(
        string title,
        List<(int Index, PickItem Item)> matches,
        int position,
        string query,
        bool filter)
    {
        var rows = new List<IRenderable>();
        var width = ContentWidth();
        var visible = Math.Max(4, SafeWindowHeight() - HeaderHeight - FooterHeight - 8);
        var start = 0;
        if (matches.Count > visible)
            start = Math.Clamp(position - visible / 2, 0, matches.Count - visible);

        var end = Math.Min(matches.Count, start + visible);
        for (var i = start; i < end; i++)
        {
            var item = matches[i].Item;
            var chosen = i == position;

            var label = item.Label;
            if (label.Length > width - 16) label = label[..Math.Max(1, width - 17)] + "…";

            var line = chosen
                ? $"[aqua]❯[/] [bold white]{Markup.Escape(label)}[/]"
                : $"  [{(item.Active ? "aqua" : "silver")}]{Markup.Escape(label)}[/]";

            var badge = item.Badge ?? (item.Active ? "active" : "");
            if (badge.Length > 0)
            {
                var pad = Math.Max(2, width - 10 - label.Length - badge.Length);
                line += new string(' ', pad) + $"[grey]{Markup.Escape(badge)}[/]";
            }

            rows.Add(new Markup(line));

            if (chosen && !string.IsNullOrWhiteSpace(item.Detail))
                rows.Add(new Markup("    [grey]" + Markup.Escape(Truncate(item.Detail!, width - 12)) + "[/]"));
        }

        if (matches.Count == 0)
            rows.Add(new Markup("[grey]nothing matches[/]"));

        rows.Add(new Text(""));
        rows.Add(new Markup(
            $"[grey]{(filter ? "type to filter · " : "")}↑↓ move · ⏎ select · Esc close[/]"));

        var heading = query.Length == 0 ? title : $"{title} · {query}";
        return new SpectrePanel(new Rows(rows))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Gold1),
            Header = new PanelHeader(" " + Markup.Escape(heading) + " ", Justify.Left),
            Padding = new SpectrePadding(1, 0, 1, 0),
        };
    }

    private void ToggleSidebar()
    {
        lock (_gate) _sidebar = !_sidebar;
    }

    private void ToggleToolDetails()
    {
        lock (_gate) _toolDetails = !_toolDetails;
    }

    /// <summary>Callers hold <c>_gate</c>.</summary>
    private List<string> HistoryFor(string prompt)
    {
        if (!_historyByPrompt.TryGetValue(prompt, out var history))
            _historyByPrompt[prompt] = history = [];
        return history;
    }

    private void Navigate(int delta)
    {
        lock (_gate)
        {
            var history = HistoryFor(_prompt);
            if (history.Count == 0) return;
            _histCursor = Math.Clamp(_histCursor + delta, 0, history.Count);
            _input = _histCursor == history.Count ? "" : history[_histCursor];
            _cursor = _input.Length;
        }
    }

    private void MoveCursor(int target)
    {
        lock (_gate) _cursor = Math.Clamp(target, 0, _input.Length);
    }

    private void Complete()
    {
        lock (_gate)
        {
            _input = Completion.Next(_input);
            _cursor = _input.Length;
        }
    }

    private void Scroll(int delta)
    {
        lock (_gate)
        {
            var max = Math.Max(0, FlattenLocked().Count - PageSize());
            _scrollFromBottom = Math.Clamp(_scrollFromBottom + delta, 0, max);
        }
    }

    private static int WordLeft(string text, int cursor)
    {
        var i = Math.Clamp(cursor, 0, text.Length);
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--;
        return i;
    }

    private static int WordRight(string text, int cursor)
    {
        var i = Math.Clamp(cursor, 0, text.Length);
        while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i;
    }

    private static int PageSize()
    {
        var size = SafeWindowHeight() - HeaderHeight - FooterHeight - 2;
        return Math.Max(3, size);
    }

    public BarrierChoice Ask(string thought, string action)
    {
        if (!string.IsNullOrWhiteSpace(thought))
        {
            Write(thought, ConsoleColor.DarkCyan, EntryStyle.Thought);
            Write("");
        }

        Write(action, ConsoleColor.White, EntryStyle.Tool);
        Write("[⏎] run   [s] step   [e] edit   [q] stop", ConsoleColor.DarkGray);
        Write("");

        if (!_interactive) return BarrierChoice.Run;

        lock (_gate) _status = "awaiting your approval";
        try
        {
            while (true)
            {
                ConsoleKeyInfo key;
                try { key = Console.ReadKey(intercept: true); }
                catch (InvalidOperationException) { return BarrierChoice.Quit; }

                switch (key.Key)
                {
                    case ConsoleKey.Enter: return BarrierChoice.Run;
                    case ConsoleKey.S: return BarrierChoice.Step;
                    case ConsoleKey.E: return BarrierChoice.Edit;
                    case ConsoleKey.Q: return BarrierChoice.Quit;
                    case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                        return BarrierChoice.Quit;
                }
            }
        }
        finally
        {
            lock (_gate) _status = "ready";
        }
    }

    private void RenderLoop(CancellationToken token)
    {
        var initial = BuildLayout(_sidebar);
        AnsiConsole.Live(initial)
            .AutoClear(false)
            .Overflow(VerticalOverflow.Crop)
            .Start(ctx =>
            {
                _layout = initial;
                _layoutSidebar = _sidebar;

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        lock (_gate)
                        {
                            if (_busy) _frame++;

                            if (_layoutSidebar != _sidebar || _layout is null)
                            {
                                _layout = BuildLayout(_sidebar);
                                _layoutSidebar = _sidebar;
                                ctx.UpdateTarget(_layout);
                            }

                            _layout["header"].Update(_header ?? new Text(""));
                            if (_sidebar) _layout["sidebar"].Update(BuildSidebar());
                            ContentTarget(_layout).Update(BuildContent());
                            _layout["footer"].Update(BuildFooter());
                        }
                        ctx.Refresh();
                    }
                    catch
                    {
                        // a resize or a transient console error must not tear down the UI
                    }

                    Thread.Sleep(70);
                }
            });
    }

    private static Layout BuildLayout(bool sidebar)
    {
        var body = new Layout("body");
        var root = new Layout("root").SplitRows(
            new Layout("header").Size(HeaderHeight),
            body,
            new Layout("footer").Size(FooterHeight));

        if (sidebar)
            body.SplitColumns(new Layout("sidebar").Size(SidebarWidth), new Layout("content"));

        return root;
    }

    private Layout ContentTarget(Layout layout) => _sidebar ? layout["content"] : layout["body"];

    private IRenderable BuildSidebar()
    {
        var rows = new List<IRenderable>();
        var inner = Math.Max(8, SafeWindowHeight() - HeaderHeight - FooterHeight - 2);

        void Section(string name)
        {
            if (rows.Count + 1 >= inner) return;
            rows.Add(new Text(""));
            rows.Add(new Markup($"[grey]{name}[/]"));
        }

        void Value(string label, string value)
        {
            if (rows.Count >= inner) return;
            rows.Add(new Markup(
                $"[grey]{label,-8}[/] [silver]{Markup.Escape(Truncate(value, SidebarWidth - 12))}[/]"));
        }

        Section("context");
        Value("model", _model);
        Value("server", _server);
        Value("project", _project);
        Value("approve", _approvals);

        var toolRoom = inner - rows.Count - 10;
        if (toolRoom >= 3)
        {
            Section("tools");
            var shown = 0;
            for (var i = 0; i < _tools.Count && rows.Count < inner - 6; i += 2)
            {
                var line = _tools[i];
                if (i + 1 < _tools.Count) line += "  " + _tools[i + 1];
                rows.Add(new Markup($"[aqua]{Markup.Escape(Truncate(line, SidebarWidth - 6))}[/]"));
                shown = Math.Min(i + 2, _tools.Count);
            }

            if (shown < _tools.Count && rows.Count < inner - 6)
                rows.Add(new Markup($"[grey]+{_tools.Count - shown} more[/]"));
        }

        Section("keys");
        rows.Add(new Markup("[aqua]⌃K[/]  [grey]commands[/]"));
        rows.Add(new Markup("[aqua]⌃O[/]  [grey]settings[/]"));
        rows.Add(new Markup("[aqua]⇧⇥[/]  [grey]approvals[/]"));
        rows.Add(new Markup("[aqua]F1[/]  [grey]help[/]"));
        rows.Add(new Markup("[aqua]⌃L[/]  [grey]clear[/]"));
        rows.Add(new Markup("[aqua]⌃T[/]  [grey]tool output[/]"));
        rows.Add(new Markup("[aqua]⌃B[/]  [grey]hide rail[/]"));

        if (rows.Count > inner) rows.RemoveRange(inner, rows.Count - inner);
        while (rows.Count < inner) rows.Add(new Text(""));

        return new SpectrePanel(new Rows(rows))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey23),
            Padding = new SpectrePadding(1, 0, 1, 0),
        };
    }

    private IRenderable BuildContent()
    {
        if (_overlay is not null) return _overlay;

        var room = PageSize();
        var lines = FlattenLocked();
        var end = Math.Max(0, lines.Count - _scrollFromBottom);
        var start = Math.Max(0, end - room);
        var slice = lines.GetRange(start, end - start);

        var rows = new List<IRenderable>();
        rows.Add(new Text(""));
        if (_transcript.Count == 0)
        {
            rows.Add(new Markup("[grey]Ask anything, or press[/] [aqua]⌃K[/] [grey]for commands.[/]"));
            rows.Add(new Text(""));
            rows.Add(new Markup("[grey]tips[/]"));
            rows.Add(new Markup("  [aqua]⌃K[/]  [grey]command palette[/]"));
            rows.Add(new Markup("  [aqua]⌃O[/]  [grey]settings, no typing required[/]"));
            rows.Add(new Markup("  [aqua]⇧⇥[/]  [grey]cycle ask → writes → all[/]"));
        }
        else
        {
            foreach (var entry in slice)
            {
                if (entry.Style == EntryStyle.ToolOutput && !_toolDetails)
                    continue;
                rows.Add(RenderEntry(entry));
            }
        }

        var header = _scrollFromBottom > 0
            ? new PanelHeader($" ↑ {_scrollFromBottom} ", Justify.Left)
            : null;

        return new SpectrePanel(new Rows(rows))
        {
            Border = BoxBorder.None,
            Header = header,
            Padding = new SpectrePadding(1, 0, 1, 0),
        };
    }

    private static IRenderable RenderEntry(Entry entry)
    {
        var text = entry.Text.Length == 0 ? " " : entry.Text;
        var escaped = Markup.Escape(text);
        var pad = new string(' ', entry.Indent);

        if (!entry.Head || entry.Style == EntryStyle.Plain)
            return new Text(pad + text, new Style(entry.Color));

        return entry.Style switch
        {
            EntryStyle.User => new Markup($"[aqua]❯[/] [white]{escaped}[/]"),
            EntryStyle.Assistant => new Markup($"[gold1]⏺[/] [white]{escaped}[/]"),
            EntryStyle.Tool => new Markup($"[grey]⏺[/] [silver]{escaped}[/]"),
            EntryStyle.ToolOutput => new Markup($"[grey]  ⎿ {escaped}[/]"),
            EntryStyle.Thought => new Markup($"[grey]✻ {escaped}[/]"),
            EntryStyle.Notice => new Markup($"[gold1]✻[/] [yellow]{escaped}[/]"),
            EntryStyle.Error => new Markup($"[red]⏺ {escaped}[/]"),
            _ => new Text(pad + text, new Style(entry.Color)),
        };
    }

    private List<Entry> FlattenLocked()
    {
        var width = ContentWidth();
        var flat = new List<Entry>();
        foreach (var entry in _transcript)
        {
            if (entry.Text.Length == 0)
            {
                flat.Add(entry);
                continue;
            }

            var room = Math.Max(12, width - entry.Indent);
            var remaining = entry.Text;
            var head = true;
            while (remaining.Length > room)
            {
                var cut = remaining.LastIndexOf(' ', Math.Min(room, remaining.Length - 1));
                if (cut <= 0) cut = room;
                flat.Add(entry with { Text = remaining[..cut], Head = head });
                remaining = remaining[cut..].TrimStart(' ');
                head = false;
            }
            flat.Add(entry with { Text = remaining, Head = head });
        }
        return flat;
    }

    private int ContentWidth()
    {
        int window;
        try { window = Console.WindowWidth; } catch { window = 120; }
        var chrome = _sidebar ? SidebarWidth + 8 : 8;
        return Math.Max(20, window - chrome);
    }

    private static int IndentFor(EntryStyle style) => style switch
    {
        EntryStyle.User or EntryStyle.Assistant or EntryStyle.Tool or EntryStyle.Notice or EntryStyle.Error => 2,
        EntryStyle.Thought => 2,
        EntryStyle.ToolOutput => 4,
        _ => 0,
    };

    private IRenderable BuildFooter()
    {
        var width = Math.Max(20, ContentWidth() - 8);
        var text = _input;
        var cursor = Math.Clamp(_cursor, 0, text.Length);
        var offset = Math.Max(0, cursor - width);
        var visible = text[offset..Math.Min(text.Length, offset + width)];
        var local = Math.Clamp(cursor - offset, 0, visible.Length);
        var before = visible[..local];
        var at = local < visible.Length ? visible[local].ToString() : " ";
        var after = local < visible.Length ? visible[(local + 1)..] : "";
        var label = _prompt.Length > 0 ? $"[grey]{Markup.Escape(_prompt)}[/] " : "";

        var placeholder = text.Length == 0
            ? "[grey]Ask anything… (/ for commands)[/]"
            : "";

        var inputLine = new Markup(
            $"[aqua]❯[/] {label}[white]{Markup.Escape(before)}[/]" +
            $"[black on silver]{Markup.Escape(at)}[/]" +
            $"[white]{Markup.Escape(after)}[/]{placeholder}");

        var box = new SpectrePanel(new Rows(inputLine, new Markup(HintLine())))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(_busy ? Color.Gold1 : Color.Grey23),
            Padding = new SpectrePadding(1, 0, 1, 0),
            Width = Math.Max(24, ContentWidth()),
        };

        // The rail occupies the left column of the body. Padding the footer by the
        // same amount keeps the prompt under the conversation instead of under the rail.
        IRenderable footer = new Rows(box, BuildStatus());
        return _sidebar
            ? new Padder(footer, new SpectrePadding(SidebarWidth, 0, 0, 0))
            : footer;
    }

    private string HintLine()
    {
        if (_overlay is not null) return $"[grey]{Markup.Escape(_overlayHint)}[/]";

        if (_input.Length == 0)
            return "[grey]⏎ send · ⇧⇥ approvals · ⌃K commands · ⌃O settings · ⌃L clear · F1 help[/]";

        if (!_input.StartsWith('/'))
            return "[grey]⏎ send · ↑↓ history · Esc clear · ⌃C empty / exit[/]";

        var space = _input.IndexOf(' ');
        if (space < 0)
        {
            var matches = CommandLine.Known
                .Where(c => c.Name.StartsWith(_input, StringComparison.OrdinalIgnoreCase))
                .Take(4)
                .ToList();

            if (matches.Count == 0) return "[grey]no such command · F1 help[/]";

            return string.Join("  ", matches.Select(c =>
                $"[aqua]{Markup.Escape(c.Name)}[/] [grey]{Markup.Escape(Truncate(c.Summary, 34))}[/]"));
        }

        var head = _input[..space].Trim();
        var partial = _input[(space + 1)..];
        if (partial.Contains(' ')) return "[grey]Tab completes · ⏎ send[/]";

        var names = Completion.Arguments(head, partial);
        if (names.Count == 0) return "[grey]Tab completes · ⏎ send[/]";

        return "[grey]Tab:[/] " + string.Join("  ", names.Take(6).Select(n => $"[aqua]{Markup.Escape(n)}[/]"));
    }

    private IRenderable BuildStatus()
    {
        var spin = _busy ? Frames[_frame % Frames.Length] : "·";
        var seconds = _busy ? (DateTime.UtcNow - _busySince).TotalSeconds : 0;
        var status = _busy
            ? char.ToUpperInvariant(_status.Length > 0 ? _status[0] : ' ') + _status[1..] + "…"
            : _status;

        var left = $"[aqua]{spin}[/] [grey]{Markup.Escape(status)}[/]";

        var middle = "";
        var middlePlain = "";
        if (_approvals.Length > 0)
        {
            middlePlain = $"approvals {_approvals}";
            middle = $"  [grey]·[/]  [grey]approvals[/] [silver]{Markup.Escape(_approvals)}[/]";
        }
        if (!_toolDetails)
        {
            if (middle.Length > 0) middle += " [grey]·[/] ";
            middle += "[grey]tool output hidden[/]";
            middlePlain += (middlePlain.Length > 0 ? " · " : "") + "tool output hidden";
        }

        var usage = new List<string>();
        if (_messages > 0) usage.Add($"{_messages} msg");
        if (_approxTokens > 0) usage.Add($"~{FormatTokens(_approxTokens)} tok");
        if (seconds > 0) usage.Add($"{seconds:0.0}s");
        var rightPlain = string.Join(" · ", usage);

        var used = spin.Length + 1 + status.Length + middlePlain.Length + rightPlain.Length;
        var gap = Math.Max(2, ContentWidth() - used);

        var line = left + middle + new string(' ', gap) + $"[grey]{Markup.Escape(rightPlain)}[/]";
        return new Markup(line);
    }

    private static string FormatTokens(int tokens) =>
        tokens >= 1000 ? (tokens / 1000.0).ToString("0.0") + "k" : tokens.ToString();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..Math.Max(1, max - 1)] + "…";

    private static IEnumerable<string> Split(string text) =>
        text.Replace("\r\n", "\n").Split('\n');

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        try { _render?.Wait(1500); } catch { }
        _cts.Dispose();

        if (_controlCAsInput)
        {
            try { Console.TreatControlCAsInput = false; } catch { }
        }
    }

    private static int SafeWindowHeight()
    {
        try { return Console.WindowHeight; } catch { return 32; }
    }

    private static Spectre.Console.Color ToColor(ConsoleColor color) => color switch
    {
        ConsoleColor.Black => Color.Black,
        ConsoleColor.DarkRed => Color.Maroon,
        ConsoleColor.DarkGreen => Color.Green,
        ConsoleColor.DarkYellow => Color.Olive,
        ConsoleColor.DarkBlue => Color.Navy,
        ConsoleColor.DarkMagenta => Color.Purple,
        ConsoleColor.DarkCyan => Color.Teal,
        ConsoleColor.Gray => Color.Silver,
        ConsoleColor.DarkGray => Color.Grey,
        ConsoleColor.Red => Color.Red,
        ConsoleColor.Green => Color.Lime,
        ConsoleColor.Yellow => Color.Yellow,
        ConsoleColor.Blue => Color.Blue,
        ConsoleColor.Magenta => Color.Fuchsia,
        ConsoleColor.Cyan => Color.Aqua,
        ConsoleColor.White => Color.White,
        _ => Color.Default,
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    private static void EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var handle = GetStdHandle(-11);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
            if (!GetConsoleMode(handle, out var mode)) return;
            SetConsoleMode(handle, mode | 0x0004);
        }
        catch { }
    }
}

/// <summary>
/// Tab completion for slash commands and their first argument. Kept out of the
/// render loop: it is pure text in, text out.
/// </summary>
internal static class Completion
{
    /// <summary>The next, more complete version of <paramref name="input"/>.</summary>
    public static string Next(string input)
    {
        if (!input.StartsWith('/')) return input;

        var space = input.IndexOf(' ');
        if (space < 0)
        {
            var names = CommandLine.Known
                .Where(c => c.AllNames.Any(n => n.StartsWith(input, StringComparison.OrdinalIgnoreCase)))
                .Select(c => c.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Complete(input, names, append: " ");
        }

        var head = input[..space].Trim();
        var partial = input[(space + 1)..];
        if (partial.Contains(' ')) return input;

        var candidates = Arguments(head, partial);
        return Complete(input, candidates, append: " ");
    }

    /// <summary>Argument candidates for a command, filtered by the partial word.</summary>
    public static List<string> Arguments(string command, string partial)
    {
        var name = CommandLine.Parse(command + " ").Command?.Name ?? command;

        IEnumerable<string> candidates = name switch
        {
            "/config" => Setting.All.Select(s => s.Name),
            "/approve" => ["ask", "writes", "all"],
            _ => [],
        };

        return candidates
            .Where(c => c.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Completes to the common prefix of the matches; with a single match it also
    /// opens the argument, and pressing Tab again cycles through the alternatives.
    /// </summary>
    private static string Complete(string input, List<string> matches, string append)
    {
        if (matches.Count == 0) return input;

        var token = Token(input);
        var start = input.Length - token.Length;
        var head = input[..start];

        // A single match is unambiguous, so open its value straight away.
        if (matches.Count == 1)
            return head + matches[0] + append;

        var prefix = CommonPrefix(matches);
        if (prefix.Length > token.Length)
            return head + prefix;

        var current = matches.FindIndex(m => m.Equals(token, StringComparison.OrdinalIgnoreCase));
        var next = current < 0 ? 0 : (current + 1) % matches.Count;
        return head + matches[next];
    }

    private static string Token(string input)
    {
        var space = input.LastIndexOf(' ');
        return space < 0 ? input : input[(space + 1)..];
    }

    private static string CommonPrefix(List<string> values)
    {
        var prefix = values[0];
        foreach (var value in values.Skip(1))
        {
            var length = 0;
            while (length < prefix.Length && length < value.Length &&
                   char.ToLowerInvariant(prefix[length]) == char.ToLowerInvariant(value[length]))
                length++;
            prefix = prefix[..length];
        }
        return prefix;
    }
}
