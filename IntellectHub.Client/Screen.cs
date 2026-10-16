using System.Runtime.InteropServices;
using System.Text;

namespace IntellectHub.Client;

public sealed class Screen : IDisposable
{
    public static readonly ConsoleColor Raw = (ConsoleColor)(-1);

    private sealed record Line(string Text, ConsoleColor Color);

    private static readonly string[] Frames = { "·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢" };

    private readonly object _gate = new();
    private readonly List<Line> _lines = new();
    private readonly List<string> _submitted = new();
    private readonly Timer _ticker;

    private string _status = "";
    private string _input = "";
    private DateTime _busySince;
    private int _frame;
    private int _scroll;
    private int _cursor = -1;
    private bool _busy;
    private bool _disposed;

    public Screen()
    {
        EnableVirtualTerminal();
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        Console.Write("\x1b[?1049h\x1b[2J\x1b[H");
        _ticker = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Write(string text, ConsoleColor color = ConsoleColor.Gray)
    {
        lock (_gate)
        {
            foreach (var part in text.Replace("\r\n", "\n").Split('\n'))
                _lines.Add(new Line(part, color));
        }
        Render();
    }

    public void WriteRaw(string ansi)
    {
        lock (_gate) _lines.Add(new Line(ansi, Raw));
        Render();
    }

    public void Clear()
    {
        lock (_gate) { _lines.Clear(); _scroll = 0; }
        Render();
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
        _ticker.Change(0, 90);
    }

    public void Idle()
    {
        _ticker.Change(Timeout.Infinite, Timeout.Infinite);
        lock (_gate) _busy = false;
        Render();
    }

    public string? ReadLine()
    {
        lock (_gate)
        {
            _input = "";
            _cursor = _submitted.Count;
            _scroll = 0;
        }
        Render();

        while (true)
        {
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(intercept: true); }
            catch (InvalidOperationException) { return null; }

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    lock (_gate)
                    {
                        var text = _input.Trim();
                        _input = "";
                        if (text.Length > 0 && (_submitted.Count == 0 || _submitted[^1] != text))
                            _submitted.Add(text);
                        _cursor = _submitted.Count;
                        return text;
                    }

                case ConsoleKey.Backspace:
                    lock (_gate) { if (_input.Length > 0) _input = _input[..^1]; }
                    Render();
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

                case ConsoleKey.End:
                    lock (_gate) _scroll = 0;
                    Render();
                    break;

                case ConsoleKey.Escape:
                    lock (_gate) _input = "";
                    Render();
                    break;

                case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    return null;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        lock (_gate) _input += key.KeyChar;
                        Render();
                    }
                    break;
            }
        }
    }

    private void Navigate(int delta)
    {
        lock (_gate)
        {
            if (_submitted.Count == 0) return;
            _cursor = Math.Clamp(_cursor + delta, 0, _submitted.Count);
            _input = _cursor == _submitted.Count ? "" : _submitted[_cursor];
        }
        Render();
    }

    private static int PageSize()
    {
        try { return Math.Max(4, Console.WindowHeight - 4); }
        catch { return 10; }
    }

    private void Scroll(int delta)
    {
        lock (_gate) _scroll = Math.Max(0, _scroll + delta);
        Render();
    }

    private void Tick()
    {
        lock (_gate) _frame++;
        Render();
    }

    private void Render()
    {
        lock (_gate)
        {
            if (_disposed) return;

            int width, height;
            try
            {
                width = Math.Max(24, Console.WindowWidth);
                height = Math.Max(8, Console.WindowHeight);
            }
            catch { return; }

            var wrapped = Wrap(width);
            var historyRows = height - 2;

            var maxScroll = Math.Max(0, wrapped.Count - historyRows);
            _scroll = Math.Clamp(_scroll, 0, maxScroll);

            var end = wrapped.Count - _scroll;
            var start = Math.Max(0, end - historyRows);
            var count = Math.Max(0, end - start);

            var sb = new StringBuilder();
            sb.Append("\x1b[H\x1b[0J");

            for (var i = 0; i < historyRows; i++)
            {
                sb.Append("\x1b[2K");
                if (i < count)
                {
                    var line = wrapped[start + i];
                    if (line.Color != Raw) sb.Append(Ansi(line.Color));
                    sb.Append(line.Text).Append("\x1b[0m");
                }
                sb.Append("\r\n");
            }

            sb.Append("\x1b[2K").Append(StatusLine()).Append("\r\n");
            sb.Append("\x1b[2K").Append(Ansi(ConsoleColor.Cyan)).Append("you > ").Append("\x1b[0m");

            var room = Math.Max(8, width - 8);
            var shown = _input.Length <= room ? _input : _input[^room..];
            sb.Append(shown).Append("\x1b[?25h");

            Console.Write(sb.ToString());
        }
    }

    private List<Line> Wrap(int width)
    {
        var wrapped = new List<Line>(_lines.Count);
        foreach (var line in _lines)
        {
            if (line.Color == Raw || line.Text.Length == 0)
            {
                wrapped.Add(line);
                continue;
            }

            for (var i = 0; i < line.Text.Length; i += width)
                wrapped.Add(new Line(line.Text.Substring(i, Math.Min(width, line.Text.Length - i)), line.Color));
        }
        return wrapped;
    }

    private string StatusLine()
    {
        if (_busy)
        {
            var spin = Frames[_frame % Frames.Length];
            var seconds = (DateTime.UtcNow - _busySince).TotalSeconds;
            return $"\x1b[96m{spin}\x1b[0m \x1b[90m{_status}  {seconds:0.0}s\x1b[0m";
        }

        if (_scroll > 0)
            return $"\x1b[90m↑ {_scroll} lines back  ·  End to return\x1b[0m";

        return "\x1b[90m/help · /models · /config · /clear · /exit\x1b[0m";
    }

    private static string Ansi(ConsoleColor color) => color switch
    {
        ConsoleColor.Black => "\x1b[30m",
        ConsoleColor.DarkRed => "\x1b[31m",
        ConsoleColor.DarkGreen => "\x1b[32m",
        ConsoleColor.DarkYellow => "\x1b[33m",
        ConsoleColor.DarkBlue => "\x1b[34m",
        ConsoleColor.DarkMagenta => "\x1b[35m",
        ConsoleColor.DarkCyan => "\x1b[36m",
        ConsoleColor.Gray => "\x1b[37m",
        ConsoleColor.DarkGray => "\x1b[90m",
        ConsoleColor.Red => "\x1b[91m",
        ConsoleColor.Green => "\x1b[92m",
        ConsoleColor.Yellow => "\x1b[93m",
        ConsoleColor.Blue => "\x1b[94m",
        ConsoleColor.Magenta => "\x1b[95m",
        ConsoleColor.Cyan => "\x1b[96m",
        ConsoleColor.White => "\x1b[97m",
        _ => "\x1b[0m",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ticker.Change(Timeout.Infinite, Timeout.Infinite);
        _ticker.Dispose();
        Console.Write("\x1b[?25h\x1b[?1049l");
    }

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
