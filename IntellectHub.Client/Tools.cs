using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using System.Windows.Forms;

namespace IntellectHub.Client;

public sealed record ToolResult(string Output, bool IsError = false);

public sealed class ToolRegistry
{
    private readonly string _workspace;
    private readonly ClientConfig? _config;

    public ToolRegistry(string workspace, ClientConfig? config = null)
    {
        _workspace = Path.GetFullPath(workspace);
        _config = config;
    }

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
        Spec("write", "Create or overwrite a UTF-8 text file in the workspace. Use this tool whenever the user asks you to create, generate, write, or save a file (code, documentation, markdown, config, etc.). Provide the full file content in the 'content' argument.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "File path relative to the workspace or absolute. Use forward slashes." },
                    ["content"] = new JsonObject { ["type"] = "string", ["description"] = "Complete file content to write. For large files, include the entire content in one call." },
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
        Spec("question", "Ask the user for clarification when requirements are ambiguous or a decision is needed.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["question"] = new JsonObject { ["type"] = "string", ["description"] = "The question for the user." },
                },
                ["required"] = new JsonArray("question"),
            }),
        Spec("browser_navigate", "Navigate to a URL in a headless browser (WebView2).",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["url"] = new JsonObject { ["type"] = "string", ["description"] = "URL to open." },
                },
                ["required"] = new JsonArray("url"),
            }),
        Spec("browser_screenshot", "Take a screenshot of the current browser page and save it to a file.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Output PNG file path relative to the workspace or absolute." },
                },
                ["required"] = new JsonArray("path"),
            }),
        Spec("browser_click", "Click an element on the current page by CSS selector.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["selector"] = new JsonObject { ["type"] = "string", ["description"] = "CSS selector of the element to click." },
                },
                ["required"] = new JsonArray("selector"),
            }),
        Spec("r2_upload", "Upload a file from the workspace to Cloudflare R2 object storage.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "File to upload, relative to the workspace or absolute." },
                    ["object"] = new JsonObject { ["type"] = "string", ["description"] = "Object name to store it under, e.g. reports/summary.md. Slashes are part of the name." },
                    ["contentType"] = new JsonObject { ["type"] = "string", ["description"] = "MIME type; guessed from the file extension when omitted." } },
                ["required"] = new JsonArray("path", "object"),
            }),
        Spec("r2_download", "Download an object from Cloudflare R2 into the workspace.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["object"] = new JsonObject { ["type"] = "string", ["description"] = "Object name to fetch." },
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Where to write it, relative to the workspace or absolute." } },
                ["required"] = new JsonArray("object", "path"),
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
            // Validate required arguments before dispatching. A missing required
            // argument is a client error — we must not silently truncate or
            // corrupt files because the model forgot an argument.
            var missing = RequiredArgsMissing(call.Name, args);
            if (missing.Count > 0)
                return new ToolResult($"Missing required arguments: {string.Join(", ", missing)}", IsError: true);

            return call.Name switch
            {
                "read" => Read(GetString(args, "path")),
                "write" => Write(GetString(args, "path"), GetString(args, "content")),
                "glob" => Glob(GetString(args, "pattern")),
                "bash" => await BashAsync(GetString(args, "command"), ct),
                "edit" => Edit(GetString(args, "path"), GetString(args, "oldString"), GetString(args, "newString")),
                "grep" => Grep(GetString(args, "pattern"), GetString(args, "path")),
                "check" => Check(GetString(args, "path")),
                "question" => Question(GetString(args, "question")),
                "browser_navigate" => await BrowserNavigateAsync(GetString(args, "url")),
                "browser_screenshot" => await BrowserScreenshotAsync(GetString(args, "path")),
                "browser_click" => await BrowserClickAsync(GetString(args, "selector")),
                "r2_upload" => await R2UploadAsync(GetString(args, "path"), GetString(args, "object"), GetString(args, "contentType"), ct),
                "r2_download" => await R2DownloadAsync(GetString(args, "object"), GetString(args, "path"), ct),
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
        
        // Backup before overwrite
        if (File.Exists(full))
        {
            var backupDir = Path.Combine(_workspace, ".backup");
            Directory.CreateDirectory(backupDir);
            var backupPath = Path.Combine(backupDir, Path.GetFileName(full) + "." + DateTime.Now.ToString("yyyyMMddHHmmss"));
            File.Copy(full, backupPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full) ?? _workspace);
        // Explicit UTF-8 without BOM so Cyrillic and other non-ASCII text is
        // preserved correctly on Windows (where the default ANSI code page can
        // mangle non-Latin characters).
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new ToolResult($"Wrote {content.Length} chars to {path} (backup created)");
    }

    private ToolResult Edit(string path, string oldString, string newString)
    {
        var full = Resolve(path);
        if (!File.Exists(full)) return new ToolResult($"File not found: {path}", IsError: true);

        // Backup before edit
        var backupDir = Path.Combine(_workspace, ".backup");
        Directory.CreateDirectory(backupDir);
        var backupPath = Path.Combine(backupDir, Path.GetFileName(full) + "." + DateTime.Now.ToString("yyyyMMddHHmmss"));
        File.Copy(full, backupPath);

        var text = File.ReadAllText(full);
        var firstIndex = text.IndexOf(oldString, StringComparison.Ordinal);
        if (firstIndex < 0) return new ToolResult("oldString not found.", IsError: true);
        
        var secondIndex = text.IndexOf(oldString, firstIndex + 1, StringComparison.Ordinal);
        if (secondIndex >= 0)
            return new ToolResult($"oldString is not unique (found at {firstIndex} and {secondIndex}); add more context.", IsError: true);

        File.WriteAllText(full, text[..firstIndex] + newString + text[(firstIndex + oldString.Length)..]);
        return new ToolResult($"Edited {path} (backup created)");
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
        
        // Exclude common build/version control directories
        var ignoredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
            { ".git", "bin", "obj", "node_modules", ".vs" };

        IEnumerable<string> files = File.Exists(root)
            ? [root]
            : Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => ignoredDirs.Contains(part)))
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

    private ToolResult Question(string question)
    {
        // This tool will be handled by the UI layer to prompt the user
        return new ToolResult($"QUESTION: {question}");
    }

    private ToolResult Glob(string pattern)
    {
        var matches = new List<string>();
        var ignoredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
            { ".git", "bin", "obj", "node_modules", ".vs" };

        foreach (var file in Directory.EnumerateFiles(_workspace, "*", SearchOption.AllDirectories))
        {
            if (file.Split(Path.DirectorySeparatorChar).Any(part => ignoredDirs.Contains(part))) continue;
            
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
        
        // Timeout after 30 seconds
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
        
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(linkedCts.Token);
            await process.WaitForExitAsync(linkedCts.Token);

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
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            return new ToolResult("Command timed out after 30 seconds.", IsError: true);
        }
    }

    private async Task<ToolResult> BrowserNavigateAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new ToolResult("url is empty", IsError: true);

        try
        {
            var session = await BrowserSession.Instance;
            await session.NavigateAsync(url);
            return new ToolResult($"Opened {url}");
        }
        catch (Exception ex)
        {
            return new ToolResult($"Browser navigate failed: {ex.Message}", IsError: true);
        }
    }

    private async Task<ToolResult> BrowserScreenshotAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new ToolResult("path is empty", IsError: true);

        try
        {
            var session = await BrowserSession.Instance;
            var full = Resolve(path);
            await session.ScreenshotAsync(full);
            return new ToolResult($"Screenshot saved to {path}");
        }
        catch (Exception ex)
        {
            return new ToolResult($"Browser screenshot failed: {ex.Message}", IsError: true);
        }
    }

    private async Task<ToolResult> BrowserClickAsync(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return new ToolResult("selector is empty", IsError: true);

        try
        {
            var session = await BrowserSession.Instance;
            await session.ClickAsync(selector);
            return new ToolResult($"Clicked {selector}");
        }
        catch (Exception ex)
        {
            return new ToolResult($"Browser click failed: {ex.Message}", IsError: true);
        }
    }

    /// <summary>
    /// Uploads a workspace file to the R2 bucket. The endpoint comes from the
    /// storage connection, so the bucket does not have to be named in the call.
    /// </summary>
    private async Task<ToolResult> R2UploadAsync(string path, string objectName, string contentType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(objectName))
            return new ToolResult("object name is empty", IsError: true);

        var full = Resolve(path);
        if (!File.Exists(full)) return new ToolResult($"File not found: {path}", IsError: true);

        var ready = StorageReady(out var endpoint, out var failure);
        if (!ready) return new ToolResult(failure!, IsError: true);

        var credentials = StorageConfig.Load(endpoint!);
        var bytes = await File.ReadAllBytesAsync(full, ct);
        var type = string.IsNullOrWhiteSpace(contentType) ? GuessContentType(full) : contentType.Trim();

        try
        {
            using var client = new StorageClient(credentials);
            var url = await client.UploadAsync(objectName, bytes, type, ct);
            return new ToolResult($"Uploaded {path} → {url} ({bytes.Length} bytes, {type})");
        }
        catch (Exception ex)
        {
            return new ToolResult($"R2 upload failed: {ex.Message}", IsError: true);
        }
    }

    /// <summary>Downloads an object from the R2 bucket into the workspace.</summary>
    private async Task<ToolResult> R2DownloadAsync(string objectName, string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(objectName))
            return new ToolResult("object name is empty", IsError: true);

        var ready = StorageReady(out var endpoint, out var failure);
        if (!ready) return new ToolResult(failure!, IsError: true);

        var credentials = StorageConfig.Load(endpoint!);

        try
        {
            using var client = new StorageClient(credentials);
            var bytes = await client.DownloadAsync(objectName, ct);
            if (bytes is null) return new ToolResult($"No such object: {objectName}", IsError: true);

            var full = Resolve(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? _workspace);
            await File.WriteAllBytesAsync(full, bytes, ct);
            return new ToolResult($"Downloaded {objectName} → {path} ({bytes.Length} bytes)");
        }
        catch (Exception ex)
        {
            return new ToolResult($"R2 download failed: {ex.Message}", IsError: true);
        }
    }

    /// <summary>
    /// Checks that the bucket is reachable and the credentials are there, and says
    /// which piece is missing when they are not.
    /// </summary>
    private bool StorageReady(out string? endpoint, out string? failure)
    {
        endpoint = _config?.StorageConnection?.Url;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            failure = "no R2 endpoint configured — set one with /connect cloudflare-r2";
            return false;
        }

        var missing = StorageConfig.Load(endpoint).Missing();
        if (missing.Length > 0)
        {
            failure = $"R2 is not configured: {missing} is missing — set IH_R2_ACCESS_KEY_ID and IH_R2_SECRET_ACCESS_KEY, " +
                      "or store them in secrets.json as r2:accessKeyId and r2:secretAccessKey";
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>The MIME type for a file, guessed from its extension.</summary>
    private static string GuessContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "text/javascript",
            ".cs" => "text/plain",
            ".xml" => "application/xml",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };

    /// <summary>
    /// Maps a tool-supplied path onto the workspace.
    ///
    /// A leading slash means "the workspace root", not the drive root. Models write
    /// <c>/index.html</c> the way they would write a URL, and Path.Combine treats a
    /// leading separator as "discard everything before it", which sent the write to
    /// <c>C:\index.html</c> and then refused it for escaping the workspace.
    ///
    /// A genuinely absolute path outside the workspace is still refused.
    /// </summary>
    private string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("path is empty");

        var relative = path.Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (relative.Length == 0)
            throw new InvalidOperationException("path names no file");

        var full = Path.GetFullPath(Path.Combine(_workspace, relative));

        // Compared against the root plus a separator, so a sibling directory whose
        // name merely starts with the workspace name is not mistaken for one inside.
        var root = _workspace.EndsWith(Path.DirectorySeparatorChar)
            ? _workspace
            : _workspace + Path.DirectorySeparatorChar;

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path escapes workspace: {path}");

        return full;
    }

    private static string GetString(JsonObject args, string name) =>
        args.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var s)
            ? s
            : "";

    private static ToolSpec Spec(string name, string description, JsonNode schema) =>
        new() { Name = name, Description = description, InputSchema = schema };

    /// <summary>
    /// Returns the names of required arguments that are missing or empty for a
    /// tool call. Used to prevent silent data loss (e.g. write without content
    /// would truncate a file to zero bytes).
    /// </summary>
    private static List<string> RequiredArgsMissing(string toolName, JsonObject args)
    {
        var missing = new List<string>();
        bool Has(string name) =>
            args.TryGetPropertyValue(name, out var node) &&
            node is JsonValue value &&
            value.TryGetValue<string>(out var s) &&
            !string.IsNullOrWhiteSpace(s);

        return toolName switch
        {
            "read" when !Has("path") => new List<string> { "path" },
            "write" when !Has("path") || !Has("content") =>
                new List<string> { "path", "content" }.Where(m => !Has(m)).ToList(),
            "edit" when !Has("path") || !Has("oldString") || !Has("newString") =>
                new List<string> { "path", "oldString", "newString" }.Where(m => !Has(m)).ToList(),
            "glob" when !Has("pattern") => new List<string> { "pattern" },
            "bash" when !Has("command") => new List<string> { "command" },
            "grep" when !Has("pattern") => new List<string> { "pattern" },
            "check" when !Has("path") => new List<string> { "path" },
            "question" when !Has("question") => new List<string> { "question" },
            "browser_navigate" when !Has("url") => new List<string> { "url" },
            "browser_screenshot" when !Has("path") => new List<string> { "path" },
            "browser_click" when !Has("selector") => new List<string> { "selector" },
            "r2_upload" when !Has("path") || !Has("object") =>
                new List<string> { "path", "object" }.Where(m => !Has(m)).ToList(),
            "r2_download" when !Has("object") || !Has("path") =>
                new List<string> { "object", "path" }.Where(m => !Has(m)).ToList(),
            _ => missing,
        };
    }
}
