using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IntellectHub.Client;

public sealed record ToolResult(string Output, bool IsError = false);

public sealed class ToolRegistry
{
    private readonly string _workspace;

    public ToolRegistry(string workspace) => _workspace = Path.GetFullPath(workspace);

    public IReadOnlyList<ToolSpec> Specs =>
    [
        Spec("read", "Read a UTF-8 text file from the workspace.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "File path relative to the workspace or absolute." },
                },
                ["required"] = new JsonArray("path"),
            }),
        Spec("write", "Create or overwrite a UTF-8 text file in the workspace.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string" },
                    ["content"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray("path", "content"),
            }),
        Spec("glob", "List files matching a glob pattern (e.g. **/*.cs).",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["pattern"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray("pattern"),
            }),
        Spec("bash", "Run a shell command (PowerShell on Windows) in the workspace.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["command"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray("command"),
            }),
        Spec("edit", "Replace the first occurrence of oldString with newString in a workspace file.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string" },
                    ["oldString"] = new JsonObject { ["type"] = "string", ["description"] = "Exact text to replace; must appear exactly once." },
                    ["newString"] = new JsonObject { ["type"] = "string", ["description"] = "Replacement text." },
                },
                ["required"] = new JsonArray("path", "oldString", "newString"),
            }),
        Spec("grep", "Search file contents with a regular expression and return matching lines.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = ".NET regular expression." },
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "File or directory to search; defaults to the workspace root." },
                },
                ["required"] = new JsonArray("pattern"),
            }),
        Spec("check", "Validate a workspace file: parse JSON/XML or report basic file facts.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray("path"),
            }),
    ];

    public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
    {
        JsonObject args;
        try
        {
            args = JsonNode.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments) as JsonObject
                   ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            return new ToolResult($"Invalid arguments: {ex.Message}", IsError: true);
        }

        try
        {
            return call.Name switch
            {
                "read" => Read(GetString(args, "path")),
                "write" => Write(GetString(args, "path"), GetString(args, "content")),
                "glob" => Glob(GetString(args, "pattern")),
                "bash" => await BashAsync(GetString(args, "command"), ct),
                "edit" => Edit(GetString(args, "path"), GetString(args, "oldString"), GetString(args, "newString")),
                "grep" => Grep(GetString(args, "pattern"), GetString(args, "path")),
                "check" => Check(GetString(args, "path")),
                _ => new ToolResult($"Unknown tool: {call.Name}", IsError: true),
            };
        }
        catch (Exception ex)
        {
            return new ToolResult(ex.Message, IsError: true);
        }
    }

    private ToolResult Read(string path)
    {
        var full = Resolve(path);
        if (!File.Exists(full)) return new ToolResult($"File not found: {path}", IsError: true);
        return new ToolResult(File.ReadAllText(full));
    }

    private ToolResult Write(string path, string content)
    {
        var full = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full) ?? _workspace);
        File.WriteAllText(full, content);
        return new ToolResult($"Wrote {content.Length} chars to {path}");
    }

    private ToolResult Edit(string path, string oldString, string newString)
    {
        var full = Resolve(path);
        if (!File.Exists(full)) return new ToolResult($"File not found: {path}", IsError: true);

        var text = File.ReadAllText(full);
        var index = text.IndexOf(oldString, StringComparison.Ordinal);
        if (index < 0) return new ToolResult("oldString not found.", IsError: true);
        if (text.IndexOf(oldString, index + 1, StringComparison.Ordinal) >= 0)
            return new ToolResult("oldString is not unique; add more context.", IsError: true);

        File.WriteAllText(full, text[..index] + newString + text[(index + oldString.Length)..]);
        return new ToolResult($"Edited {path}");
    }

    private ToolResult Grep(string pattern, string path)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern);
        }
        catch (ArgumentException ex)
        {
            return new ToolResult($"Invalid pattern: {ex.Message}", IsError: true);
        }

        var root = string.IsNullOrWhiteSpace(path) ? _workspace : Resolve(path);
        IEnumerable<string> files = File.Exists(root)
            ? [root]
            : Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                : [];

        var hits = new List<string>();
        foreach (var file in files)
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch (IOException) { continue; }

            var rel = Path.GetRelativePath(_workspace, file).Replace('\\', '/');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i])) continue;
                hits.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
                if (hits.Count >= 200) break;
            }
            if (hits.Count >= 200) break;
        }

        return hits.Count == 0
            ? new ToolResult("No matches.")
            : new ToolResult(string.Join('\n', hits));
    }

    private ToolResult Check(string path)
    {
        var full = Resolve(path);
        if (!File.Exists(full)) return new ToolResult($"File not found: {path}", IsError: true);

        var text = File.ReadAllText(full);
        var lineCount = text.Length == 0 ? 0 : text.Split('\n').Length;
        var info = new StringBuilder($"{path}: {text.Length} chars, {lineCount} lines");

        var ext = Path.GetExtension(full).ToLowerInvariant();
        switch (ext)
        {
            case ".json":
                try { JsonDocument.Parse(text).Dispose(); info.Append("\njson: valid"); }
                catch (JsonException ex) { info.Append("\njson: invalid - " + ex.Message); }
                break;
            case ".xml":
                try { System.Xml.Linq.XDocument.Parse(text); info.Append("\nxml: valid"); }
                catch (System.Xml.XmlException ex) { info.Append("\nxml: invalid - " + ex.Message); }
                break;
        }

        return new ToolResult(info.ToString());
    }

    private ToolResult Glob(string pattern)
    {
        var matches = new List<string>();
        foreach (var file in Directory.EnumerateFiles(_workspace, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(_workspace, file).Replace('\\', '/');
            if (Matches(pattern, rel)) matches.Add(rel);
            if (matches.Count >= 200) break;
        }
        return matches.Count == 0
            ? new ToolResult("No files matched.")
            : new ToolResult(string.Join('\n', matches));
    }

    private static bool Matches(string pattern, string path)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(path, regex);
    }

    private async Task<ToolResult> BashAsync(string command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell" : "/bin/sh",
            WorkingDirectory = _workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
        }
        else
        {
            psi.ArgumentList.Add("-c");
        }
        psi.ArgumentList.Add(command);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var output = new StringBuilder();
        var outText = await stdout;
        var errText = await stderr;
        if (outText.Length > 0) output.Append(outText);
        if (errText.Length > 0)
        {
            if (output.Length > 0) output.Append('\n');
            output.Append(errText);
        }
        if (output.Length == 0) output.Append($"(exit {process.ExitCode})");
        return new ToolResult(output.ToString(), IsError: process.ExitCode != 0);
    }

    private string Resolve(string path)
    {
        var full = Path.GetFullPath(Path.Combine(_workspace, path));
        if (!full.StartsWith(_workspace, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path escapes workspace: {path}");
        return full;
    }

    private static string GetString(JsonObject args, string name) =>
        args.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var s)
            ? s
            : "";

    private static ToolSpec Spec(string name, string description, JsonNode schema) =>
        new() { Name = name, Description = description, InputSchema = schema };
}
