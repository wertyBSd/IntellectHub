using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace IntellectHub.Client;

public enum BarrierChoice
{
    Run,
    Step,
    Edit,
    Quit,
}

public sealed class Ui : IDisposable
{
    private static readonly string[] Frames = { "·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢" };
    // Driven by the command table so the sidebar cannot drift from what is actually
// accepted at the prompt.
    private static readonly string[] Commands =
        CommandLine.Known.Where(c => !c.Hidden).Select(c => c.Name).ToArray();

    private const int HeaderHeight = 5;
    private const int FooterHeight = 3;
    private const int SidebarWidth = 26;
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

    private IRenderable? _header;
    private Task? _render;
    private string _prompt = "you > ";
    private string _input = "";
    private string _status = "ready";
    private DateTime _busySince = DateTime.UtcNow;
    private int _frame;
    private int _histCursor;
    private int _scrollFromBottom;
    private int _liveIndex = -1;
    private bool _busy;
    private bool _disposed;

    private sealed record Entry(string Text, Color Color);

    public Ui()
    {
        EnableVirtualTerminal();
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        _interactive = AnsiConsole.Profile.Capabilities.Interactive;
        if (_interactive)
            _render = Task.Run(() => RenderLoop(_cts.Token));
    }

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

    public void Write(string text, ConsoleColor color = ConsoleColor.Gray)
    {
        var tint = ToColor(color);
        lock (_gate)
        {
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                _transcript.Add(new Entry(line, tint));
            if (_transcript.Count > TranscriptLimit)
                _transcript.RemoveRange(0, _transcript.Count - TranscriptLimit);
            _scrollFromBottom = 0;
        }

        if (!_interactive)
            Console.WriteLine(text);
    }

    public void BeginStream(ConsoleColor color = ConsoleColor.White)
    {
        lock (_gate)
        {
            _transcript.Add(new Entry("", ToColor(color)));
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

    public string? ReadLine(string prompt = "you > ")
    {
        if (!_interactive)
        {
            Console.Write(prompt);
            return Console.ReadLine();
        }

        lock (_gate)
        {
            _prompt = prompt;
            _input = "";
            _histCursor = HistoryFor(prompt).Count;
            _scrollFromBottom = 0;
        }

        while (true)
        {
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(intercept: true); }
            catch (InvalidOperationException) { return null; }

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                {
                    string text;
                    lock (_gate)
                    {
                        text = _input.Trim();
                        var history = HistoryFor(prompt);
                        if (text.Length > 0 && (history.Count == 0 || history[^1] != text))
                            history.Add(text);
                        _histCursor = history.Count;
                        _input = "";
                    }
                    return text;
                }

                case ConsoleKey.Backspace:
                    lock (_gate) { if (_input.Length > 0) _input = _input[..^1]; }
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
                    Scroll(int.MaxValue);
                    break;

                case ConsoleKey.End:
                    Scroll(int.MinValue);
                    break;

                case ConsoleKey.Escape:
                    lock (_gate) _input = "";
                    break;

                case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    return null;

                default:
                    if (!char.IsControl(key.KeyChar))
                        lock (_gate) _input += key.KeyChar;
                    break;
            }
        }
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

    private static int PageSize()
    {
        var size = SafeWindowHeight() - HeaderHeight - FooterHeight - 4;
        return Math.Max(3, size);
    }

    public BarrierChoice Ask(string thought, string action)
    {
        if (!string.IsNullOrWhiteSpace(thought))
        {
            Write("thought", ConsoleColor.DarkGray);
            Write("  " + thought, ConsoleColor.DarkCyan);
            Write("");
        }

        Write("action", ConsoleColor.DarkGray);
        Write("  " + action, ConsoleColor.Yellow);
        Write("  [Enter] run  [s] step  [e] edit  [q] stop", ConsoleColor.DarkGray);
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
        var layout = new Layout("root")
            .SplitRows(
                new Layout("header").Size(HeaderHeight),
                new Layout("body"),
                new Layout("footer").Size(FooterHeight));
        layout["body"].SplitColumns(
            new Layout("sidebar").Size(SidebarWidth),
            new Layout("content"));

        AnsiConsole.Live(layout)
            .AutoClear(false)
            .Overflow(VerticalOverflow.Crop)
            .Start(ctx =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        lock (_gate)
                        {
                            if (_busy) _frame++;
                            layout["header"].Update(_header ?? new Text(""));
                            layout["sidebar"].Update(BuildSidebar());
                            layout["content"].Update(BuildContent());
                            layout["footer"].Update(BuildFooter());
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

    private IRenderable BuildSidebar()
    {
        var rows = new List<IRenderable>();

        var bodyHeight = Math.Max(8, SafeWindowHeight() - HeaderHeight - FooterHeight - 2);
        var toolRoom = bodyHeight - 14;
        if (toolRoom >= 1)
        {
            rows.Add(new Markup("[gold1]tools[/]"));

            if (_tools.Count == 0)
            {
                rows.Add(new Markup("  [grey](none)[/]"));
            }
            else
            {
                const int columns = 2;
                const int colWidth = 10;
                var linesNeeded = (_tools.Count + columns - 1) / columns;
                var shown = Math.Min(linesNeeded, toolRoom);
                var truncated = shown * columns < _tools.Count;
                var slots = truncated ? shown * columns - 1 : _tools.Count;

                for (var r = 0; r < shown; r++)
                {
                    var line = new StringBuilder();
                    for (var c = 0; c < columns; c++)
                    {
                        var index = r * columns + c;
                        if (index >= slots) break;
                        var name = _tools[index];
                        if (name.Length >= colWidth) name = name[..(colWidth - 1)];
                        line.Append(name.PadRight(colWidth));
                    }
                    rows.Add(new Markup("  [aqua]" + Markup.Escape(line.ToString().TrimEnd()) + "[/]"));
                }

                if (truncated)
                    rows.Add(new Markup($"  [grey]+{_tools.Count - slots} more[/]"));
            }
        }

        rows.Add(new Text(""));
        rows.Add(new Markup("[gold1]commands[/]"));
        foreach (var command in Commands)
            rows.Add(new Markup($"  [silver]{Markup.Escape(command)}[/]"));

        rows.Add(new Text(""));
        rows.Add(new Markup("[gold1]keys[/]"));
        rows.Add(new Markup("  [grey]Up/Down    history[/]"));
        rows.Add(new Markup("  [grey]PgUp/PgDn   scroll[/]"));
        rows.Add(new Markup("  [grey]Esc        clear input[/]"));

        return new Panel(new Rows(rows))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            Padding = new Padding(1, 0, 1, 0),
        };
    }

    private IRenderable BuildContent()
    {
        var room = PageSize();
        var lines = FlattenLocked();
        var end = Math.Max(0, lines.Count - _scrollFromBottom);
        var start = Math.Max(0, end - room);
        var slice = lines.GetRange(start, end - start);

        var rows = new List<IRenderable>();
        if (slice.Count == 0)
            rows.Add(new Markup("[grey]Type a message, or /help for commands.[/]"));
        else
            foreach (var entry in slice)
                rows.Add(new Text(entry.Text.Length == 0 ? " " : entry.Text, new Style(entry.Color)));

        var header = _scrollFromBottom > 0
            ? new PanelHeader($" conversation  ↑ {_scrollFromBottom} ", Justify.Left)
            : new PanelHeader(" conversation ", Justify.Left);

        return new Panel(new Rows(rows))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            Header = header,
            Padding = new Padding(1, 0, 1, 0),
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

            var remaining = entry.Text;
            while (remaining.Length > width)
            {
                var cut = remaining.LastIndexOf(' ', Math.Min(width, remaining.Length - 1));
                if (cut <= 0) cut = width;
                flat.Add(entry with { Text = remaining[..cut] });
                remaining = remaining[cut..].TrimStart(' ');
            }
            flat.Add(entry with { Text = remaining });
        }
        return flat;
    }

    private static int ContentWidth()
    {
        int window;
        try { window = Console.WindowWidth; } catch { window = 120; }
        return Math.Max(20, window - SidebarWidth - 6);
    }

    private IRenderable BuildFooter()
    {
        var input = new Markup(
            $"[aqua]{Markup.Escape(_prompt)}[/][white]{Markup.Escape(_input)}[/][grey]▏[/]");

        var spin = _busy ? Frames[_frame % Frames.Length] : "·";
        var seconds = _busy ? (DateTime.UtcNow - _busySince).TotalSeconds : 0;
        var left = new Markup($"[aqua]{spin}[/] [grey]{Markup.Escape(_status)}[/]");
        var context = new Markup("[grey]ctx[/] [aqua]▓▓▓▓▓░░░░░[/]");
        var time = new Markup(seconds > 0 ? $"[grey]{seconds:0.0}s[/]" : "");

        var bar = new Grid();
        bar.AddColumn(new GridColumn());
        bar.AddColumn(new GridColumn());
        bar.AddColumn(new GridColumn().RightAligned());
        bar.AddRow(left, context, time);

        return new Rows(input, new Text(""), bar);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        try { _render?.Wait(1500); } catch { }
        _cts.Dispose();
    }

    private static int SafeWindowHeight()
    {
        try { return Console.WindowHeight; } catch { return 32; }
    }

    private static Color ToColor(ConsoleColor color) => color switch
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
