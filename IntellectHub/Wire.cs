#if DEBUG
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace IntellectHub;

/// <summary>
/// Full-corridor wire trace for Debug builds. Records everything that crosses a
/// process boundary — client to gateway, gateway to DeepSeek, DeepSeek back again —
/// so a manual test can be replayed offline without guessing what went where.
///
/// Every entry point is <see cref="ConditionalAttribute"/>-annotated, so a Release
/// build compiles the call sites away entirely: no argument evaluation, no files,
/// no chance of leaking a prompt into a shipped binary.
///
/// Lines are buffered and flushed off the hot path: SSE can emit hundreds of chunks
/// per request and a synchronous append would show up as upstream latency.
///
/// Credentials are redacted on the way in. The logs are meant to be shareable, and
/// the DeepSeek bearer token plus the whole cookie jar sit in these headers.
/// </summary>
internal static class Wire
{
    /// <summary>Tag written on every line so server and client files can be told apart.</summary>
    private const string Component = "SERVER";

    /// <summary>Prompts are capped well above the 80k production ceiling, so nothing real is cut.</summary>
    private const int MaxBlockChars = 150_000;

    /// <summary>Rotate the day's file once it gets this big.</summary>
    private const long MaxFileBytes = 32L * 1024 * 1024;

    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly AsyncLocal<string?> AmbientTrace = new();

    private static readonly Regex SecretJson = new(
        "\"(?<k>[^\"]*(?:token|cookie|api[_-]?key|authorization|secret|password|dliq|leim)[^\"]*)\"\\s*:\\s*\"(?<v>[^\"]*)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> SecretHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "x-api-key", "Cookie", "Set-Cookie",
        "x-hif-dliq", "x-hif-leim",
    };

    /// <summary>
    /// Without the relaxed encoder every Cyrillic character comes out as a \uXXXX
    /// escape, which makes a Cyrillic trace unreadable — which is the whole point
    /// of the trace.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int _fileSeq;
    private static System.Threading.Timer? _flushTimer;

    /// <summary>
    /// Starts the flush pump. This has to run before the first entry is queued:
    /// the timer lives here rather than next to the file creation precisely because
    /// a timer created by the flush can never schedule that flush.
    /// </summary>
    static Wire()
    {
        _flushTimer = new System.Threading.Timer(_ => Flush(), null, 300, 300);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            _flushTimer?.Dispose();
            Flush();
        };
    }

    /// <summary>Where the trace lands. Surfaced in the UI console on first write.</summary>
    public static string LogDirectory { get; } = ResolveAndEnsureDirectory();

    /// <summary>Full path of the active file, or null when nothing could be opened.</summary>
    public static string? FilePath { get; private set; }

    /// <summary>
    /// The id shared with the caller. Set once per inbound request; every nested
    /// line inherits it, which is what stitches client, gateway and DeepSeek together.
    /// </summary>
    public static string? TraceId
    {
        get => AmbientTrace.Value;
        set => AmbientTrace.Value = value;
    }

    /// <summary>Mints a short id when the client did not send one of its own.</summary>
    public static string NewTraceId(string prefix = "srv") =>
        $"{prefix}-{Environment.TickCount64:x8}{Interlocked.Increment(ref _fileSeq) & 0xff:x2}";

    public static void Write(string tag, string message) => Enqueue(tag, message, null);

    public static void Write(string tag, string format, params object?[] args) =>
        Enqueue(tag, args is { Length: > 0 } ? SafeFormat(format, args) : format, null);

    /// <summary>A timing footer for a span that is already known to be finished.</summary>
    public static void Timing(string tag, Stopwatch clock, string summary) =>
        Enqueue(tag, $"{summary} | {clock.ElapsedMilliseconds} ms", null);

    public static void Timing(string tag, Stopwatch clock, string format, params object?[] args) =>
        Enqueue(tag, (args is { Length: > 0 } ? SafeFormat(format, args) : format) + $" | {clock.ElapsedMilliseconds} ms", null);

    /// <summary>
    /// Dumps a multi-line payload as an indented block, so JSON stays copy-pasteable
    /// and log line numbers still mean something.
    /// </summary>
    public static void Block(string tag, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            Enqueue(tag, "(empty)", null);
            return;
        }

        var body = Redact(text);
        if (body.Length > MaxBlockChars)
            body = body[..MaxBlockChars] + $"\n... [truncated, {text.Length - MaxBlockChars} more chars]";

        Enqueue(tag, null, body);
    }

    /// <summary>Same as <see cref="Block"/> but indents the payload as JSON first.</summary>
    public static void Json(string tag, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            Enqueue(tag, "(empty)", null);
            return;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Block(tag, System.Text.Json.JsonSerializer.Serialize(doc.RootElement, PrettyJson));
        }
        catch (System.Text.Json.JsonException)
        {
            // Not every body is JSON (SSE frames, for one). The raw text is still useful.
            Block(tag, json);
        }
    }

    /// <summary>Headers with credentials replaced. Bodies of interest are dumped separately.</summary>
    public static void Headers(string tag, System.Collections.Specialized.NameValueCollection? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            Write(tag, "(no headers)");
            return;
        }

        foreach (string? name in headers.AllKeys)
        {
            if (string.IsNullOrEmpty(name)) continue;
            Write(tag, "{0}: {1}", name, Show(name, headers[name]));
        }
    }

    /// <summary>Same, for an outbound <see cref="System.Net.Http.HttpRequestMessage"/>.</summary>
    public static void Headers(string tag, System.Net.Http.Headers.HttpHeaders? headers)
    {
        if (headers is null)
        {
            Write(tag, "(no headers)");
            return;
        }

        foreach (var (name, values) in headers)
            Write(tag, "{0}: {1}", name, Show(name, string.Join(", ", values)));
    }

    private static string Show(string name, string? value) =>
        SecretHeaders.Contains(name) ? Mask(value) : Collapse(value);

    /// <summary>Masks a secret down to its shape, which is what you need to debug with.</summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        return value.Length <= 8
            ? $"<{value.Length} chars>"
            : $"{value[..4]}…{value[^4..]} <{value.Length} chars>";
    }

    /// <summary>Sweeps credential-looking JSON fields out of a payload before it is written.</summary>
    public static string Redact(string text) => SecretJson.Replace(text, m =>
        $"\"{m.Groups["k"].Value}\":\"<{m.Groups["v"].Value.Length} chars>\"");

    /// <summary>Forces the buffer out to disk. Called on shutdown and before the test is read.</summary>
    public static void Flush()
    {
        try
        {
            WriteToDisk();
        }
        catch
        {
            // Tracing must never take the app down.
        }
    }

    private static void Enqueue(string tag, string? message, string? block)
    {
        var id = TraceId;
        var head = id is null
            ? $"[{DateTime.Now:HH:mm:ss.fff}] {Component,-6} {tag,-14}"
            : $"[{DateTime.Now:HH:mm:ss.fff}] {Component,-6} {tag,-14} [{id}]";

        var sb = new StringBuilder(head);
        if (!string.IsNullOrEmpty(message)) sb.Append(' ').Append(message);

        if (block is not null)
        {
            foreach (var line in block.Replace("\r\n", "\n").Split('\n'))
                sb.Append('\n').Append("        ").Append(line);
        }

        Pending.Enqueue(sb.ToString());
        if (Pending.Count >= 256) Flush();
    }

    private static string SafeFormat(string format, object?[] args)
    {
        try
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    private static string Collapse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        return value.Length <= 400 ? value : value[..400] + $"… (+{value.Length - 400} chars)";
    }

    private static void WriteToDisk()
    {
        if (Pending.IsEmpty) return;

        var batch = new List<string>(Math.Min(Pending.Count, 1024));
        while (batch.Count < 1024 && Pending.TryDequeue(out var line)) batch.Add(line);
        if (batch.Count == 0) return;

        var path = EnsureFile();
        if (path is null)
        {
            Pending.Clear();
            return;
        }

        var payload = new StringBuilder();
        foreach (var line in batch) payload.Append(line).Append('\n');

        lock (typeof(Wire))
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes)
                    Roll(path);

                File.AppendAllText(path, payload.ToString(), Encoding.UTF8);
            }
            catch (IOException)
            {
                // Another writer holds the file. The batch is dropped rather than retried
                // in a loop; the next flush continues from the following entry.
            }
        }
    }

    private static void Roll(string path)
    {
        var stamp = $"{DateTime.Now:HHmmssfff}";
        File.Move(path, $"{Path.GetFileNameWithoutExtension(path)}-{stamp}{Path.GetExtension(path)}", overwrite: false);
    }

    private static string? EnsureFile()
    {
        if (FilePath is not null && File.Exists(FilePath)) return FilePath;

        try
        {
            Directory.CreateDirectory(LogDirectory);
        }
        catch
        {
            return null;
        }

        var path = Path.Combine(LogDirectory, $"server-{DateTime.Now:yyyyMMdd}.log");
        FilePath = path;

        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path,
                    $"=== IntellectHub server wire trace === started {DateTime.Now:yyyy-MM-dd HH:mm:ss} === " +
                    $"pid {Environment.ProcessId} ==={Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            return null;
        }

        return path;
    }

    private static string ResolveDirectory()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("IH_TRACE_DIR"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IntellectHub", "debug"),
            Path.Combine(AppContext.BaseDirectory, "debug-logs"),
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch
            {
                // Try the next location.
            }
        }

        return Path.Combine(Path.GetTempPath(), "IntellectHub-debug");
    }

    private static string ResolveAndEnsureDirectory()
    {
        var dir = ResolveDirectory();
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // If even the fallback fails, use temp path
            dir = Path.Combine(Path.GetTempPath(), "IntellectHub-debug");
            try { Directory.CreateDirectory(dir); } catch { }
        }
        return dir;
    }
}
#else
using System.Diagnostics;

/// <summary>
/// Release stub. Identical surface to the Debug implementation, but every entry
/// point is stripped by the compiler, so tracing costs a Release build nothing.
/// </summary>
internal static class Wire
{
    public static string? TraceId { get; set; }
    public static string LogDirectory => "";

    public static string NewTraceId(string prefix = "srv") => "";

    public static string Mask(string? value) => value ?? "";

    [Conditional("DEBUG")]
    public static void Write(string tag, string message) { }

    [Conditional("DEBUG")]
    public static void Write(string tag, string format, params object?[] args) { }

    [Conditional("DEBUG")]
    public static void Timing(string tag, Stopwatch clock, string summary) { }

    [Conditional("DEBUG")]
    public static void Timing(string tag, Stopwatch clock, string format, params object?[] args) { }

    [Conditional("DEBUG")]
    public static void Block(string tag, string? text) { }

    [Conditional("DEBUG")]
    public static void Json(string tag, string? json) { }

    [Conditional("DEBUG")]
    public static void Headers(string tag, System.Collections.Specialized.NameValueCollection? headers) { }

    [Conditional("DEBUG")]
    public static void Headers(string tag, System.Net.Http.Headers.HttpHeaders? headers) { }

    [Conditional("DEBUG")]
    public static void Flush() { }
}
#endif