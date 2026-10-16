using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IntellectHub;

/// <summary>A gateway tool call recovered from an upstream reply.</summary>
internal sealed record ParsedToolCall(string Name, string Arguments);

/// <summary>
/// Recovers tool calls from a DeepSeek reply. This is a port of the Node
/// gateway's parser: DSML (canonical and doubled-bar web variants), the
/// {"tool_call":...} envelope, fenced JSON and legacy TOOL_CALL: markers.
///
/// Everything here is deliberately strict. A reply that merely *looks* like
/// tool markup but does not parse into a well-formed, in-scope call is
/// rejected, never executed, and reported through
/// <see cref="LooksLikeToolCallMarkup"/> so the caller can repair the prompt
/// instead of leaking raw DSML to the client as assistant text.
/// </summary>
internal static partial class ToolCallParser
{
    internal const int MaxAgentToolCalls = 8;

    private const int MaxToolMarkupChars = 256 * 1024;
    private const int MaxToolArgumentChars = 128 * 1024;
    private const int MaxToolJsonCandidates = 32;
    private const int MaxDsmlParameters = 128;
    private const int MaxDsmlStructuralTags = MaxDsmlParameters * 2 + 16;
    private const int MaxDsmlTagChars = 2048;

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---- DSML tag canonicalisation -------------------------------------------------

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNameRegex();

    [GeneratedRegex(@"[|｜]+\s*DSML\s*[|｜]+|[<＜]\s*/?\s*(?:DSML)?(?:[\w.-]+:)?(?:tool[\s_-]*calls|function[\s_-]*calls|invoke)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DsmlMarkerRegex();

    // The web variant writes the wrapper as a bare "calls" (<｜｜DSML｜｜ calls>).
    // Accept it as an alias of tool_calls so the reply is understood rather than
    // rejected as malformed markup.
    [GeneratedRegex(@"^(?:(?:[A-Za-z_][\w.-]*):)?(tool[\s_-]*calls|function[\s_-]*calls|calls|invoke|parameter)\b([\s\S]*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SemanticTagRegex();

    [GeneratedRegex(@"^\|+\s*DSML\s*\|+\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DsmlPrefixRegex();

    [GeneratedRegex(@"^DSML(?=(?:tool[\s_-]*calls|function[\s_-]*calls|invoke|parameter)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DsmlNamePrefixRegex();

    [GeneratedRegex(@"^name\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NameAttrPrefixRegex();

    [GeneratedRegex(@"<([^<>]{0,1024})>", RegexOptions.CultureInvariant)]
    private static partial Regex AngleTagRegex();

    // ---- structural tag scanning ---------------------------------------------------

    [GeneratedRegex(@"^\/?(?:tool_calls|invoke|parameter|direct)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StructuralPrefixRegex();

    [GeneratedRegex(@"^(tool_calls|invoke|parameter|direct)\b([\s\S]*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalTagRegex();

    [GeneratedRegex(@"^<!\[CDATA\[([\s\S]*?)\]\]>$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CdataRegex();

    // ---- attributes ----------------------------------------------------------------

    [GeneratedRegex(@"\bname\s*=\s*([""'])([^""']+)\1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NameAttrRegex();

    [GeneratedRegex(@"\bstring\s*=\s*([""'])([^""']+)\1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StringAttrRegex();

    // ---- JSON envelope scanning ---------------------------------------------------

    [GeneratedRegex(@"```([a-zA-Z0-9#+_-]*)\s*([\s\S]*?)```", RegexOptions.CultureInvariant)]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"```([a-zA-Z0-9#+_-]*)\r?\n([\s\S]*?)```", RegexOptions.CultureInvariant)]
    private static partial Regex CodeFenceRegex();

    [GeneratedRegex(@"TOOL_CALL:\s*([\w-]+)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyToolCallRegex();

    [GeneratedRegex(@"<tool_call[^>]*>([\s\S]*?)</tool_call>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex XmlToolCallRegex();

    // ---- shape detection ----------------------------------------------------------

    [GeneratedRegex(@"TOOL_CALL:\s*[\w-]+|<\s*tool_call\b|[|｜]+\s*DSML\s*[|｜]+|[<＜]\s*/?\s*(?:DSML)?(?:[\w.-]+:)?(?:tool[\s_-]*calls|function[\s_-]*calls|invoke)\b|[""'](?:tool_call|tool_calls|function_call)[""']\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ToolCallMarkupRegex();

    // DeepSeek Web's own built-in tools. The gateway cannot run any of them, and a
    // call to one must never be forwarded to a client as an executable tool call —
    // the client has no such tool and will fail every time.
    //
    // This list is matched ONLY when the client did not offer the same name, so a
    // gateway that genuinely exposes e.g. write_file keeps working unchanged.
    [GeneratedRegex(@"^(?:web_?search|webfetch|web_?fetch|fetch_?url|browser_?search|browser_?action|browser_?click|" +
                    @"browser_?type|browser_?screenshot|browser_?navigate|execute_?code|code_?interpreter|" +
                    @"run_?commands?|shell_?command|terminal_?command|exec_?command|" +
                    @"read_?file|write_?file|edit_?file|create_?file|delete_?file|list_?files|" +
                    @"search_?files|apply_?patch|task|terminate|ask_?user)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NativeToolRegex();

    [GeneratedRegex(@"(?:^|\n)\s*(?:using\s+[\w.]+;|namespace\s+[\w.]+|public\s+(?:sealed\s+|partial\s+|static\s+|abstract\s+)*(?:class|struct|interface|enum)\b)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SourceLikeRegex();

    // ---- public API ----------------------------------------------------------------

    internal static bool HasDsmlToolMarkup(string? text) =>
        !string.IsNullOrEmpty(text) && DsmlMarkerRegex().IsMatch(text);

    internal static bool LooksLikeToolCallMarkup(string? text) =>
        !string.IsNullOrEmpty(text) && ToolCallMarkupRegex().IsMatch(text);

    internal static bool IsDeepSeekNativeTool(string? name) =>
        !string.IsNullOrEmpty(name) && NativeToolRegex().IsMatch(name);

    /// <summary>
    /// True when the model answered with a block of source code instead of a tool
    /// request. Only meaningful when tools are available.
    /// </summary>
    internal static bool LooksLikeCodeDumpInsteadOfTool(string? text)
    {
        if (string.IsNullOrEmpty(text) || LooksLikeToolCallMarkup(text)) return false;

        var codeChars = 0;
        var codeFences = 0;
        foreach (Match fence in CodeFenceRegex().Matches(text))
        {
            var lang = fence.Groups[1].Value.ToLowerInvariant();
            if (lang.Length == 0 || lang is "json" or "text" or "txt" or "markdown" or "md") continue;
            codeFences++;
            codeChars += fence.Groups[2].Value.Length;
        }

        if (codeChars >= 400 || codeFences >= 2) return true;
        if (codeFences == 1 && codeChars >= 200) return true;

        return text.Length >= 800 && SourceLikeRegex().IsMatch(text);
    }

    /// <summary>
    /// Parses a single tool call from raw reply text, or null when the text holds no
    /// well-formed call. Malformed tool markup is never coerced into a call.
    /// </summary>
    internal static ParsedToolCall? ParseToolCall(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxToolMarkupChars) return null;

        // DSML gets its own path: a half-emitted wrapper must not fall through to
        // the JSON scanners, which could otherwise execute the payload it contains.
        if (HasDsmlToolMarkup(text)) return ParseDsmlToolCall(text);

        var xml = XmlToolCallRegex().Match(text);
        if (xml.Success)
        {
            var parsed = ParseJsonToolCandidate(xml.Groups[1].Value.Trim(), allowBare: true);
            if (parsed is not null) return parsed;
        }

        // Fenced JSON. Language-tagged source fences are skipped so a code dump is
        // never scanned as a tool envelope.
        foreach (Match fence in FenceRegex().Matches(text))
        {
            var lang = fence.Groups[1].Value.ToLowerInvariant();
            if (lang.Length > 0 && lang != "json") continue;
            var parsed = ParseJsonToolCandidate(fence.Groups[2].Value.Trim(), allowBare: false);
            if (parsed is not null) return parsed;
        }

        var legacy = LegacyToolCallRegex().Match(text);
        if (legacy.Success)
        {
            var after = text[(legacy.Index + legacy.Length)..];
            var braceIdx = after.IndexOf('{');
            if (braceIdx != -1)
            {
                var rawJson = ExtractBalancedJsonAt(after, braceIdx);
                if (rawJson is not null && TryParseJsonObject(rawJson, out var legacyArgs))
                {
                    var parsed = BuildToolCall(legacy.Groups[1].Value, legacyArgs);
                    if (parsed is not null) return parsed;
                }
            }
        }

        // Scan each top-level balanced object once. Only explicit tool-call
        // envelopes are executable; bare {name, arguments} samples are not.
        foreach (var rawJson in ExtractBalancedJsonObjects(text))
        {
            var parsed = ParseJsonToolCandidate(rawJson, allowBare: false);
            if (parsed is not null) return parsed;
        }

        return null;
    }

    /// <summary>Every DSML call in the reply, in order, tolerating stacked variants.</summary>
    internal static IReadOnlyList<ParsedToolCall> ListDsmlToolCalls(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxToolMarkupChars) return [];
        if (!HasDsmlToolMarkup(text)) return [];

        var normalized = NormalizeToolMarkupTags(text);
        var scope = ExtractToolCallScope(normalized);
        if (scope is null) return [];

        var tags = ScanDsmlStructuralTags(scope);
        if (tags is null || tags.Count == 0) return [];

        var calls = new List<ParsedToolCall>();
        var consumed = new HashSet<int>();

        for (var i = 0; i < tags.Count; i++)
        {
            if (consumed.Contains(i)) continue;
            var tag = tags[i];

            if (tag.Name == "invoke" && !tag.Closing && !tag.SelfClosing)
            {
                var closeIdx = FindTagIndex(tags, i + 1,
                    candidate => candidate.Name == "invoke" && candidate.Closing);
                if (closeIdx == -1) continue;
                consumed.Add(closeIdx);

                var parsed = ParseDsmlInvoke(GetAttribute(tag.Attrs, "name"), scope[tag.End..tags[closeIdx].Start]);
                if (parsed is not null) calls.Add(parsed);
                continue;
            }

            if (tag.Name == "direct" && !tag.Closing)
            {
                var nextIdx = FindTagIndex(tags, i + 1,
                    candidate => candidate.Name is "direct" or "invoke");
                var end = nextIdx == -1 ? scope.Length : tags[nextIdx].Start;

                var parsed = ParseDsmlInvoke(GetAttribute(tag.Attrs, "name"), scope[tag.End..end]);
                if (parsed is not null) calls.Add(parsed);
            }
        }

        return calls;
    }

    /// <summary>Calls the client actually offered, de-duplicated and capped.</summary>
    internal static IReadOnlyList<ParsedToolCall> CollectAgentToolCalls(string? text, IReadOnlySet<string> allowed)
    {
        if (string.IsNullOrEmpty(text) || allowed.Count == 0) return [];

        var raw = HasDsmlToolMarkup(text) ? ListDsmlToolCalls(text) : CollectJsonToolCalls(text);

        var calls = new List<ParsedToolCall>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in raw)
        {
            if (!allowed.Contains(call.Name)) continue;
            if (!seen.Add(call.Name + "\0" + call.Arguments)) continue;
            calls.Add(call);
            if (calls.Count >= MaxAgentToolCalls) break;
        }

        return calls;
    }

    /// <summary>
    /// The call to act on. When the client offered tools, only those are
    /// considered; otherwise any well-formed call is accepted so a chat-only
    /// client does not receive raw DSML as its answer.
    /// </summary>
    internal static ParsedToolCall? SelectAgentToolCall(string? text, IReadOnlySet<string> allowed)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var calls = CollectAgentToolCalls(text, allowed);
        if (calls.Count > 0) return calls[0];

        if (allowed.Count > 0) return null;

        var listed = ListDsmlToolCalls(text);
        return (HasDsmlToolMarkup(text) ? ParseDsmlToolCall(text) : null)
               ?? (listed.Count > 0 ? listed[0] : null);
    }

    // ---- DSML implementation ------------------------------------------------------

    private static ParsedToolCall? ParseDsmlToolCall(string text)
    {
        if (text.Length == 0 || text.Length > MaxToolMarkupChars) return null;

        var normalized = NormalizeToolMarkupTags(text);
        var scope = ExtractToolCallScope(normalized);
        if (scope is null) return null;

        var tags = ScanDsmlStructuralTags(scope);
        if (tags is null || tags.Count == 0) return null;

        var first = tags[0];
        if (!string.IsNullOrWhiteSpace(scope[..first.Start])) return null;

        if (first.Name == "invoke" && !first.Closing && !first.SelfClosing)
        {
            var invokeTags = tags.Where(tag => tag.Name == "invoke").ToList();
            if (invokeTags.Count != 2 || invokeTags[0].Start != first.Start || !invokeTags[1].Closing) return null;

            var closing = invokeTags[1];
            if (!string.IsNullOrWhiteSpace(scope[closing.End..])) return null;
            if (tags.Any(tag => tag.Name is "tool_calls" or "direct")) return null;

            return ParseDsmlInvoke(GetAttribute(first.Attrs, "name"), scope[first.End..closing.Start]);
        }

        if (first.Name == "direct" && !first.Closing && !first.SelfClosing)
        {
            if (tags.Skip(1).Any(tag => tag.Name is "direct" or "invoke" or "tool_calls")) return null;
            return ParseDsmlInvoke(GetAttribute(first.Attrs, "name"), scope[first.End..]);
        }

        return null;
    }

    private static string? ExtractToolCallScope(string normalized)
    {
        var tags = ScanDsmlStructuralTags(normalized);
        if (tags is null) return null;

        var wrappers = tags.Where(tag => tag.Name == "tool_calls").ToList();
        var openings = wrappers.Where(tag => !tag.Closing).ToList();
        var closings = wrappers.Where(tag => tag.Closing).ToList();

        if (openings.Count > 0)
        {
            if (openings.Count != 1 || openings[0].SelfClosing) return null;

            var opening = openings[0];
            if (wrappers.Any(tag => tag.Closing && tag.Start < opening.End)) return null;

            if (closings.Count == 0)
            {
                // The web DSML variant often omits the closing wrapper and then
                // stacks several calls. Keep the tail as the scope.
                if (tags.Any(tag => tag.Name != "tool_calls" && tag.Start < opening.End)) return null;
                return normalized[opening.End..];
            }

            var closing = closings[^1];
            if (closing.Start < opening.End) return null;
            if (tags.Any(tag => tag.Name != "tool_calls" &&
                                (tag.Start < opening.End || tag.Start >= closing.Start))) return null;
            return normalized[opening.End..closing.Start];
        }

        // Narrow repair: tolerate a missing opening wrapper only when a closing
        // wrapper exists. A bare invoke without this sentinel is never executable.
        if (closings.Count > 0)
        {
            var closing = closings[^1];
            var invokeOpenings = tags
                .Where(tag => tag.Name == "invoke" && !tag.Closing && tag.Start < closing.Start)
                .ToList();

            if (invokeOpenings.Count == 1 && !invokeOpenings[0].SelfClosing)
            {
                var invoke = invokeOpenings[0];
                if (tags.Any(tag => tag.Name != "tool_calls" &&
                                    (tag.Start < invoke.Start || tag.Start >= closing.Start))) return null;
                return normalized[invoke.Start..closing.Start];
            }
        }

        return null;
    }

    private static ParsedToolCall? ParseDsmlInvoke(string? name, string body)
    {
        var structuralTags = ScanDsmlStructuralTags(body);
        if (structuralTags is null) return null;
        if (structuralTags.Any(tag => tag.Name != "parameter")) return null;

        var parameterTags = structuralTags.Where(tag => tag.Name == "parameter").ToList();

        var args = new JsonObject();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var parameterCount = 0;
        var cursor = 0;

        for (var i = 0; i < parameterTags.Count; i += 2)
        {
            var opening = parameterTags[i];
            var closing = i + 1 < parameterTags.Count ? parameterTags[i + 1] : null;
            if (closing is null || opening.Closing || opening.SelfClosing || !closing.Closing) return null;

            // Parameters must tile the body exactly: no stray text between them.
            if (!string.IsNullOrWhiteSpace(body[cursor..opening.Start])) return null;

            parameterCount++;
            if (parameterCount > MaxDsmlParameters) return null;

            if (!ParseDsmlParameter(opening.Attrs, body[opening.End..closing.Start], args, seenNames)) return null;
            cursor = closing.End;
        }

        if (parameterCount > 0)
        {
            if (!string.IsNullOrWhiteSpace(body[cursor..])) return null;
            return BuildToolCall(name, args);
        }

        // No <parameter> tags: the body may still be a single raw JSON object.
        var decodedBody = DecodeDsmlValue(body).Trim();
        if (decodedBody.Length == 0) return BuildToolCall(name, null);

        var objects = ExtractBalancedJsonObjects(decodedBody, 2);
        if (objects.Count != 1 || decodedBody != objects[0]) return null;
        if (!TryParseJsonObject(objects[0], out var parsed)) return null;

        return BuildToolCall(name, parsed);
    }

    private static bool ParseDsmlParameter(string attrs, string rawBody, JsonObject args, HashSet<string> seenNames)
    {
        var parameterName = GetAttribute(attrs, "name");
        if (string.IsNullOrEmpty(parameterName) ||
            !ToolNameRegex().IsMatch(parameterName) ||
            !seenNames.Add(parameterName))
            return false;

        var rawValue = DecodeDsmlParameterValue(rawBody);
        if (rawValue.Length > MaxToolArgumentChars) return false;

        var stringMode = GetAttribute(attrs, "string");
        JsonNode? value;

        if (stringMode is not null && stringMode.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            // string="false" means the body carries JSON, not a literal string.
            if (!TryParseJsonNode(rawValue.Trim(), out var parsed)) return false;
            value = parsed;
        }
        else
        {
            value = JsonValue.Create(rawValue);
        }

        args[parameterName] = value;
        return true;
    }

    private static List<DsmlTag>? ScanDsmlStructuralTags(string text)
    {
        var tags = new List<DsmlTag>();

        for (var i = 0; i < text.Length;)
        {
            if (i + 9 <= text.Length && string.CompareOrdinal(text, i, "<![CDATA[", 0, 9) == 0)
            {
                var cdataEnd = text.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                if (cdataEnd == -1) return null;
                i = cdataEnd + 3;
                continue;
            }

            if (text[i] != '<') { i++; continue; }

            var (tag, invalid) = ReadDsmlTagAt(text, i);
            if (tag is null) { i++; continue; }
            if (invalid) return null;

            tags.Add(tag);
            if (tags.Count > MaxDsmlStructuralTags) return null;
            i = tag.End;
        }

        return tags;
    }

    private static (DsmlTag? Tag, bool Invalid) ReadDsmlTagAt(string text, int start)
    {
        if (start >= text.Length || text[start] != '<') return (null, false);

        var prefixEnd = Math.Min(text.Length, start + 40);
        var prefix = text.Substring(start + 1, prefixEnd - (start + 1)).TrimStart();
        if (!StructuralPrefixRegex().IsMatch(prefix)) return (null, false);

        // Quote-aware so a '>' inside an attribute value does not end the tag.
        char? quote = null;
        var end = -1;
        var scanEnd = Math.Min(text.Length, start + MaxDsmlTagChars + 1);
        for (var i = start + 1; i < scanEnd; i++)
        {
            var ch = text[i];
            if (quote is { } open)
            {
                if (ch == open) quote = null;
                continue;
            }

            if (ch is '"' or '\'') { quote = ch; continue; }
            if (ch == '>') { end = i; break; }
        }

        if (end == -1) return (null, true);

        var token = text.Substring(start + 1, end - start - 1).Trim();

        var closing = false;
        if (token.StartsWith('/'))
        {
            closing = true;
            token = token[1..].Trim();
        }

        var selfClosing = false;
        if (!closing && token.EndsWith('/'))
        {
            selfClosing = true;
            token = token[..^1].Trim();
        }

        var match = CanonicalTagRegex().Match(token);
        if (!match.Success) return (null, false);

        var tag = new DsmlTag(
            match.Groups[1].Value.ToLowerInvariant(),
            closing ? string.Empty : match.Groups[2].Value,
            closing,
            selfClosing,
            start,
            end + 1);

        return (tag, false);
    }

    private static string NormalizeToolMarkupTags(string text)
    {
        var withAsciiAngles = text.Replace('＜', '<').Replace('＞', '>');

        return AngleTagRegex().Replace(withAsciiAngles, match =>
        {
            var canonical = CanonicalizeToolMarkupTag(match.Groups[1].Value);
            return canonical ?? match.Value;
        });
    }

    private static string? CanonicalizeToolMarkupTag(string rawTag)
    {
        var token = rawTag.Trim()
            .Replace('｜', '|')
            .Replace('“', '"').Replace('”', '"').Replace('＂', '"')
            .Replace('‘', '\'').Replace('’', '\'').Replace('＇', '\'');

        var closing = false;
        if (token.StartsWith('/'))
        {
            closing = true;
            token = token[1..].Trim();
        }

        token = DsmlPrefixRegex().Replace(token, string.Empty);

        if (token.StartsWith('/'))
        {
            closing = true;
            token = token[1..].Trim();
        }

        token = DsmlNamePrefixRegex().Replace(token, string.Empty);

        // The doubled-bar web variant puts the payload straight after the DSML
        // marker without a tag name, e.g. <｜｜DSML｜｜ name="web_search">.
        if (!closing && NameAttrPrefixRegex().IsMatch(token)) return $"<direct {token}>";

        var semantic = SemanticTagRegex().Match(token);
        if (!semantic.Success) return null;

        var localName = Regex.Replace(semantic.Groups[1].Value, @"[\s_-]", string.Empty).ToLowerInvariant();
        var canonicalName = localName switch
        {
            "toolcalls" or "functioncalls" or "calls" => "tool_calls",
            _ => localName,
        };

        var attrs = closing ? string.Empty : semantic.Groups[2].Value;
        return closing ? $"</{canonicalName}>" : $"<{canonicalName}{attrs}>";
    }

    private static string DecodeDsmlValue(string value) =>
        value.Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase)
             .Replace("&apos;", "'", StringComparison.OrdinalIgnoreCase)
             .Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
             .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase)
             .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);

    private static string DecodeDsmlParameterValue(string value)
    {
        var trimmed = value.Trim();
        var cdata = CdataRegex().Match(trimmed);
        return cdata.Success ? cdata.Groups[1].Value : DecodeDsmlValue(value);
    }

    private static string? GetAttribute(string? attrs, string attribute)
    {
        if (string.IsNullOrEmpty(attrs)) return null;

        var match = attribute switch
        {
            "name" => NameAttrRegex().Match(attrs),
            "string" => StringAttrRegex().Match(attrs),
            _ => Match.Empty,
        };

        return match.Success ? match.Groups[2].Value : null;
    }

    private static int FindTagIndex(List<DsmlTag> tags, int from, Func<DsmlTag, bool> predicate)
    {
        for (var i = Math.Max(0, from); i < tags.Count; i++)
            if (predicate(tags[i]))
                return i;

        return -1;
    }

    private sealed record DsmlTag(string Name, string Attrs, bool Closing, bool SelfClosing, int Start, int End);

    // ---- JSON envelope implementation ----------------------------------------------

    private static IReadOnlyList<ParsedToolCall> CollectJsonToolCalls(string? text)
    {
        var calls = new List<ParsedToolCall>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text) || text.Length > MaxToolMarkupChars) return calls;

        foreach (Match fence in FenceRegex().Matches(text))
        {
            var lang = fence.Groups[1].Value.ToLowerInvariant();
            if (lang.Length > 0 && lang != "json") continue;
            AbsorbJsonToolPayload(fence.Groups[2].Value.Trim(), calls, seen);
        }

        foreach (Match legacy in LegacyToolCallRegex().Matches(text))
        {
            var after = text[(legacy.Index + legacy.Length)..];
            var braceIdx = after.IndexOf('{');
            if (braceIdx == -1) continue;

            var rawJson = ExtractBalancedJsonAt(after, braceIdx);
            if (rawJson is null || !TryParseJsonObject(rawJson, out var args)) continue;

            RememberToolCall(calls, seen, BuildToolCall(legacy.Groups[1].Value, args));
        }

        foreach (var rawJson in ExtractBalancedJsonObjects(text))
            AbsorbJsonToolPayload(rawJson, calls, seen);

        return calls;
    }

    private static void AbsorbJsonToolPayload(string? raw, List<ParsedToolCall> calls, HashSet<string> seen)
    {
        if (string.IsNullOrEmpty(raw)) return;
        if (!TryParseJsonNode(raw, out var node)) return;

        if (node is JsonArray array)
        {
            if (array.Count == 0 || array.Count > MaxAgentToolCalls) return;
            foreach (var item in array)
                RememberToolCall(calls, seen, CoerceToolCallObject(item, allowBare: true));
            return;
        }

        if (node is JsonObject obj && obj.TryGetPropertyValue("tool_calls", out var list) && list is JsonArray toolArray)
        {
            // Each entry is re-wrapped so the single-call envelope rules apply to it.
            foreach (var item in toolArray)
            {
                if (item is null) continue;

                var wrapper = new JsonObject { ["tool_calls"] = new JsonArray(item.DeepClone()) };
                RememberToolCall(calls, seen, CoerceToolCallObject(wrapper, allowBare: false));
            }

            return;
        }

        RememberToolCall(calls, seen, CoerceToolCallObject(node, allowBare: false));
    }

    private static void RememberToolCall(List<ParsedToolCall> calls, HashSet<string> seen, ParsedToolCall? call)
    {
        if (call is null) return;
        if (!seen.Add(call.Name + "\0" + call.Arguments)) return;
        calls.Add(call);
    }

    private static ParsedToolCall? ParseJsonToolCandidate(string raw, bool allowBare)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (!TryParseJsonNode(raw, out var node)) return null;
        return CoerceToolCallObject(node, allowBare);
    }

    private static ParsedToolCall? CoerceToolCallObject(JsonNode? node, bool allowBare)
    {
        if (node is not JsonObject obj) return null;

        JsonObject? candidate = null;
        if (obj.TryGetPropertyValue("tool_call", out var single)) candidate = single as JsonObject;
        else if (obj.TryGetPropertyValue("function_call", out var legacy)) candidate = legacy as JsonObject;
        else if (obj.TryGetPropertyValue("tool_calls", out var many))
        {
            if (many is not JsonArray manyArray || manyArray.Count != 1) return null;
            candidate = manyArray[0] as JsonObject;
        }
        else if (allowBare)
        {
            candidate = obj;
        }

        if (candidate is null) return null;

        var fn = candidate.TryGetPropertyValue("function", out var function) ? function as JsonObject : null;
        var owner = fn ?? candidate;

        var name = NullishString(owner, "name") ?? NullishString(candidate, "name");
        var args = NullishNode(owner, "arguments")
                   ?? NullishNode(candidate, "arguments")
                   ?? NullishNode(candidate, "input");

        return BuildToolCallFromNode(name, args);
    }

    private static string? NullishString(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var value) && value is JsonValue jv && jv.TryGetValue<string>(out var text)
            ? text
            : null;

    private static JsonNode? NullishNode(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var value) ? value : null;

    private static ParsedToolCall? BuildToolCall(string? name, JsonObject? args)
    {
        var toolName = name?.Trim() ?? "";
        if (!ToolNameRegex().IsMatch(toolName)) return null;

        var serialized = args?.ToJsonString(CompactJson) ?? "{}";
        return serialized.Length > MaxToolArgumentChars ? null : new ParsedToolCall(toolName, serialized);
    }

    /// <summary>
    /// Builds a call from an already-parsed arguments node. OpenAI clients send
    /// <c>arguments</c> as a JSON *string*, so that shape is decoded here; anything
    /// that does not resolve to a JSON object is rejected.
    /// </summary>
    private static ParsedToolCall? BuildToolCallFromNode(string? name, JsonNode? args)
    {
        var toolName = name?.Trim() ?? "";
        if (!ToolNameRegex().IsMatch(toolName)) return null;

        switch (args)
        {
            case null:
                return new ParsedToolCall(toolName, "{}");

            case JsonObject obj:
                var serialized = obj.ToJsonString(CompactJson);
                return serialized.Length > MaxToolArgumentChars ? null : new ParsedToolCall(toolName, serialized);

            case JsonValue value when value.TryGetValue<string>(out var raw):
                if (raw.Length > MaxToolArgumentChars) return null;
                if (!TryParseJsonObject(raw, out var parsed)) return null;

                var reparsed = parsed!.ToJsonString(CompactJson);
                return reparsed.Length > MaxToolArgumentChars ? null : new ParsedToolCall(toolName, reparsed);

            default:
                // Arrays, numbers, booleans and literal nulls are not valid arguments.
                return null;
        }
    }

    private static bool TryParseJsonObject(string raw, out JsonObject? value)
    {
        value = null;
        if (!TryParseJsonNode(raw, out var node)) return false;
        value = node as JsonObject;
        return value is not null;
    }

    private static bool TryParseJsonNode(string raw, out JsonNode? node)
    {
        node = null;
        try
        {
            node = JsonNode.Parse(raw);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? ExtractBalancedJsonAt(string text, int startIndex)
    {
        if (startIndex < 0 || startIndex >= text.Length || text[startIndex] != '{') return null;

        var depth = 0;
        var inString = false;
        var escape = false;

        for (var i = startIndex; i < text.Length; i++)
        {
            var ch = text[i];
            if (escape) { escape = false; continue; }
            if (ch == '\\' && inString) { escape = true; continue; }
            if (ch == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(startIndex, i - startIndex + 1);
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ExtractBalancedJsonObjects(string text, int maxObjects = MaxToolJsonCandidates)
    {
        var objects = new List<string>();

        var start = -1;
        var depth = 0;
        var inString = false;
        var escape = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (start == -1)
            {
                if (ch == '{')
                {
                    start = i;
                    depth = 1;
                    inString = false;
                    escape = false;
                }
                continue;
            }

            if (escape) { escape = false; continue; }
            if (ch == '\\' && inString) { escape = true; continue; }
            if (ch == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth != 0) continue;

                objects.Add(text.Substring(start, i - start + 1));
                if (objects.Count >= maxObjects) return objects;
                start = -1;
            }
        }

        return objects;
    }
}