using System.Collections.Concurrent;

namespace IntellectHub.Tests;

/// <summary>
/// Captures the chat log while a test runs. AppLog is process-global, which is why
/// the whole assembly runs its tests serially.
/// </summary>
internal sealed class LogCapture : IDisposable
{
    private readonly ConcurrentQueue<string> _lines = new();

    public LogCapture() => AppLog.Written += OnWritten;

    private void OnWritten(LogLevel level, string message) => _lines.Enqueue(message);

    public IReadOnlyList<string> Lines => [.. _lines];

    public string Joined => string.Join("\n", _lines);

    public void Dispose() => AppLog.Written -= OnWritten;
}