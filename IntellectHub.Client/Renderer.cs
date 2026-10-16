namespace IntellectHub.Client;

public static class Renderer
{
    private const ConsoleColor Accent = ConsoleColor.Cyan;
    private const ConsoleColor Dim = ConsoleColor.DarkGray;
    private const ConsoleColor Tool = ConsoleColor.Yellow;
    private const ConsoleColor ErrorColor = ConsoleColor.Red;

    public static void User(string text)
    {
        Console.ForegroundColor = Accent;
        Console.Write("you > ");
        Console.ResetColor();
        Console.WriteLine(text);
    }

    public static void Assistant(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Console.ForegroundColor = Accent;
        Console.Write("hub > ");
        Console.ResetColor();
        Console.WriteLine(text.Trim());
    }

    public static void ToolCall(string name, string arguments)
    {
        Console.ForegroundColor = Tool;
        Console.WriteLine($"  [tool] {name} {Compact(arguments)}");
        Console.ResetColor();
    }

    public static void ToolResult(string name, string output, bool isError)
    {
        Console.ForegroundColor = isError ? ErrorColor : Dim;
        var preview = output.Length > 400 ? output[..400] + "..." : output;
        Console.WriteLine($"  [{(isError ? "fail" : "ok")}] {name}: {Compact(preview)}");
        Console.ResetColor();
    }

    public static void Info(string text)
    {
        Console.ForegroundColor = Dim;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    public static void Error(string text)
    {
        Console.ForegroundColor = ErrorColor;
        Console.WriteLine("error: " + text);
        Console.ResetColor();
    }

    private static string Compact(string text) =>
        text.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
