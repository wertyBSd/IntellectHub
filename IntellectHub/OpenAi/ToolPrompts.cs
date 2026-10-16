using System.Text;

namespace IntellectHub;

/// <summary>
/// Renders the client's tool catalogue into the flat text prompt that the DeepSeek
/// web endpoint accepts. Upstream has no tool-calling channel, so the manifest is
/// the only way the model learns that gateway tools exist.
/// </summary>
internal static class ToolPrompts
{
    public static string FormatToolDefinitions(IReadOnlyList<ToolDefinition> tools, int promptBudget)
    {
        if (tools.Count == 0) return "";

        var rawSchemaChars = 0;
        foreach (var tool in tools)
        {
            if (string.IsNullOrEmpty(tool.Function?.ParametersRaw)) continue;
            rawSchemaChars += tool.Function.ParametersRaw.Length;
        }

        // Verbose schemas are the first thing to eat the budget, so drop their
        // documentation before anything else gets cut.
        var compactSchemas = rawSchemaChars > promptBudget * 0.4;

        var text = new StringBuilder();
        text.AppendLine();
        text.AppendLine();
        text.AppendLine("--- TOOLS ---");
        text.AppendLine("Two actions, never mixed in one reply.");
        text.AppendLine(
            "Web: DeepSeek Search is already on. Answer in plain text with the findings. Do not use bash, curl, websearch, or webfetch for the web, and do not say you lack web access.");
        text.AppendLine(
            "This computer: request local tools and nothing else. One call, or several independent calls together:");
        text.AppendLine("{\"tool_call\":{\"name\":\"<function_name>\",\"arguments\":{...}}}");
        text.AppendLine(
            "{\"tool_calls\":[{\"name\":\"<function_name>\",\"arguments\":{...}},{\"name\":\"<function_name>\",\"arguments\":{...}}]}");
        text.AppendLine(
            "The gateway runs them and sends the outputs back. Do not invent those outputs. Keep arguments short.");
        text.AppendLine();
        text.AppendLine("Available functions:");

        foreach (var tool in tools)
        {
            var fn = tool.Function;
            if (fn is null || string.IsNullOrWhiteSpace(fn.Name)) continue;

            text.AppendLine();
            text.AppendLine("## " + fn.Name);

            var description = Collapse(fn.Description ?? "");
            text.AppendLine(description.Length > 500 ? description[..497] + "..." : description);

            if (!string.IsNullOrEmpty(fn.ParametersRaw))
            {
                var schema = compactSchemas ? ToolSchema.Compact(fn.ParametersRaw) : fn.ParametersRaw;
                text.AppendLine("Parameters: " + schema);
            }
        }

        text.AppendLine();
        text.AppendLine("--- END TOOLS ---");
        return text.ToString();
    }

    /// <summary>A short restatement of the tool rules, pinned to the end of the prompt.</summary>
    public static string FormatToolReminder(IReadOnlyList<ToolDefinition> tools)
    {
        var names = ToolFunctionNames(tools);
        if (names.Count == 0) return "";

        return string.Join('\n',
            "--- TOOL REMINDER ---",
            "Web: answer in text. DeepSeek Search is already on. Do not use bash or curl for the web.",
            "This computer: output only {\"tool_call\":{\"name\":\"<function_name>\",\"arguments\":{...}}} or {\"tool_calls\":[{\"name\":\"<function_name>\",\"arguments\":{...}}]}. Do not paste source files or diffs. Do not call execute_code.",
            "Available tools: " + string.Join(", ", names),
            "--- END TOOL REMINDER ---");
    }

    internal static IReadOnlyList<string> ToolFunctionNames(IReadOnlyList<ToolDefinition> tools)
    {
        var names = new List<string>();
        foreach (var tool in tools)
        {
            var name = tool.Function?.Name ?? tool.Name;
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
        }

        return names;
    }

    /// <summary>The function names the client offered, as a validation set.</summary>
    internal static HashSet<string> AllowedToolNames(IReadOnlyList<ToolDefinition> tools) =>
        ToolFunctionNames(tools).ToHashSet(StringComparer.Ordinal);

    /// <param name="toolName">
    /// The native tool the model reached for. Naming it is what stops the model
    /// from asking for the very same unreachable tool on the next round.
    /// </param>
    public static string NativeToolRepairPrompt(bool searchEnabled, string? toolName = null)
    {
        const string gatewayTool =
            "If you need the local filesystem, request exactly one gateway tool as {\"tool_call\":{\"name\":\"<function_name>\",\"arguments\":{...}}}.";

        var called = string.IsNullOrWhiteSpace(toolName) ? "execute_code or web_search" : $"the built-in tool `{toolName}`";

        return searchEnabled
            ? $"[STRICT INSTRUCTION] You called {called}. This gateway cannot run your built-in tools — do not call them again, and do not wrap them in a tool_call. DeepSeek native Web Search is already enabled: answer with those findings in plain text. {gatewayTool} Do not say you lack web access."
            : $"[STRICT INSTRUCTION] You called {called}. This gateway cannot run your built-in tools — do not call them again, and do not wrap them in a tool_call. Answer in plain text from what you already know. {gatewayTool}";
    }

    public static string MalformedMarkupRepairPrompt =>
        "[STRICT INSTRUCTION] Your previous response contained incomplete tool-call markup. Keep arguments short and output ONLY strict JSON: {\"tool_call\":{\"name\":\"<function>\",\"arguments\":{...}}}";

    public static string CodeDumpRepairPrompt =>
        "[STRICT INSTRUCTION] You pasted source code as plain text. This gateway cannot apply pasted files. Request exactly one tool to write or edit the file. Output ONLY strict JSON: {\"tool_call\":{\"name\":\"<function>\",\"arguments\":{...}}}. Put file contents in the tool arguments, not in markdown fences.";

    public static string AbandonedToolRepairPrompt =>
        "[STRICT INSTRUCTION] You stopped after a tool result. Continue the task. Output exactly one gateway tool request as {\"tool_call\":{\"name\":\"<function>\",\"arguments\":{...}}} or a complete final answer if the work is finished.";

    private static string Collapse(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }
}

/// <summary>Drops deep JSON-schema detail when the manifest would otherwise dominate the prompt.</summary>
internal static class ToolSchema
{
    public static string Compact(string schemaJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(schemaJson);
            var root = doc.RootElement;
            var sb = new StringBuilder();
            CompactObject(root, sb, 0);
            return sb.Length == 0 ? schemaJson : sb.ToString();
        }
        catch (System.Text.Json.JsonException)
        {
            return schemaJson;
        }
    }

    private static void CompactObject(System.Text.Json.JsonElement obj, StringBuilder sb, int depth)
    {
        sb.Append('{');
        var first = true;

        foreach (var property in obj.EnumerateObject())
        {
            if (property.NameEquals("description") || property.NameEquals("title")) continue;

            if (!first) sb.Append(',');
            first = false;
            sb.Append(JsonSerializerShim.Write(property.Name)).Append(':');
            CompactValue(property.Value, sb, depth + 1);
        }

        sb.Append('}');
    }

    private static void CompactValue(System.Text.Json.JsonElement value, StringBuilder sb, int depth)
    {
        switch (value.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                if (depth > 4) { sb.Append("{}"); return; }
                CompactObject(value, sb, depth);
                return;

            case System.Text.Json.JsonValueKind.Array:
                var items = new List<System.Text.Json.JsonElement>();
                foreach (var item in value.EnumerateArray()) items.Add(item);
                sb.Append('[');
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (i >= 8) { sb.Append("..."); break; }
                    CompactValue(items[i], sb, depth + 1);
                }
                sb.Append(']');
                return;

            default:
                sb.Append(value.GetRawText());
                return;
        }
    }

    private static class JsonSerializerShim
    {
        public static string Write(string value) => System.Text.Json.JsonSerializer.Serialize(value);
    }
}