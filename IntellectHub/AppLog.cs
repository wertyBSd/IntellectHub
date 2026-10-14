using System.Diagnostics;

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
    public static event Action<LogLevel, string>? Written;

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Ok(string message) => Write(LogLevel.Ok, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Muted(string message) => Write(LogLevel.Muted, message);

    private static void Write(LogLevel level, string message)
    {
        Written?.Invoke(level, message);
        Debug.WriteLine($"[{level}] {message}");
    }
}
