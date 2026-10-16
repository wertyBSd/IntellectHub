using System.Text;

namespace IntellectHub.Client;

public static class Banner
{
    public const string Version = "0.2.0";

    private static readonly string[] Logo =
    {
        "            ╲   │   ╱",
        "             ╲  │  ╱",
        "              ╲ │ ╱",
        "               ╲│╱",
        "        ────────◆────────",
        "               ╱│╲",
        "              ╱ │ ╲",
        "             ╱  │  ╲",
        "            ╱   │   ╲",
    };

    public static void Print(Screen screen, ClientConfig config)
    {
        screen.Write("");
        foreach (var line in Logo)
            screen.WriteRaw(Paint(line));
        screen.WriteRaw("");
        screen.WriteRaw("  \x1b[97mI N T E L L E C T   H U B\x1b[0m");
        screen.WriteRaw($"  \x1b[90myour private model hub   v{Version}\x1b[0m");
        screen.WriteRaw("");
        screen.Write("  server  " + config.BaseUrl, ConsoleColor.DarkGray);
        screen.Write("  model   " + config.Model, ConsoleColor.DarkGray);
        screen.Write("");
    }

    private static string Paint(string line)
    {
        var sb = new StringBuilder("  ");
        foreach (var ch in line)
        {
            sb.Append(ch switch
            {
                '◆' => "\x1b[96m",
                '│' => "\x1b[90m",
                '╲' or '╱' => "\x1b[36m",
                '─' => "\x1b[90m",
                _ => "\x1b[0m",
            });
            sb.Append(ch).Append("\x1b[0m");
        }
        return sb.ToString();
    }
}
