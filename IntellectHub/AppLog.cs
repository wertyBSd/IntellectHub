using System.Diagnostics;
using System.Text;

namespace IntellectHub;

internal enum LogLevel
{
    Info,
    Ok,
    Warn,
    Error,
    Muted
}

internal static class AppLog
{
    private static readonly object Gate = new();
    private static string? _logDirectory;

    public static event Action<LogLevel, string>? Written;

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Ok(string message) => Write(LogLevel.Ok, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Error(string message, Exception ex) => Write(LogLevel.Error, $"{message}: {ex}");
    public static void Muted(string message) => Write(LogLevel.Muted, message);

    public static string? LogDirectory => _logDirectory ??= ResolveLogDirectory();

    private static void Write(LogLevel level, string message)
    {
        Written?.Invoke(level, message);
        Debug.WriteLine($"[{level}] {message}");
        if (level == LogLevel.Error) AppendError(message);
    }

    private static void AppendError(string message)
    {
        try
        {
            var dir = LogDirectory;
            if (dir is null) return;
            var path = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private static string? ResolveLogDirectory()
    {
        var roots = new[]
        {
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            try
            {
                var dir = Path.Combine(root, "logs");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
            }
        }
        return null;
    }
}
