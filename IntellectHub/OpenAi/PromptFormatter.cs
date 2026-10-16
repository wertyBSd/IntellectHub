using System.Text;
using System.Text.Json;

namespace IntellectHub;

internal static class PromptFormatter
{
    public static string NormalizeContent(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString() ?? "";
            case JsonValueKind.Array:
                var sb = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    var text = ExtractPartText(part);
                    if (string.IsNullOrEmpty(text)) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(text);
                }
                return sb.ToString();
            case JsonValueKind.Object:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return content.ToString();
            default:
                return "";
        }
    }

    private static string ExtractPartText(JsonElement part)
    {
        if (part.ValueKind == JsonValueKind.String) return part.GetString() ?? "";
        if (part.ValueKind != JsonValueKind.Object) return "";
        if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            return text.GetString() ?? "";
        if (part.TryGetProperty("content", out var content))
            return NormalizeContent(content);
        return part.ToString();
    }

    public static (string Prompt, string SystemPrompt) Format(IReadOnlyList<ChatMessage> messages) =>
        Format(messages, 0);

    /// <summary>
    /// Flattens the conversation into plain text, skipping the first
    /// <paramref name="startIndex"/> messages. A live remote session already holds
    /// those, and sending them again would show the model the same history twice.
    /// </summary>
    public static (string Prompt, string SystemPrompt) Format(IReadOnlyList<ChatMessage> messages, int startIndex)
    {
        var system = new StringBuilder();
        var conversation = new StringBuilder();

        for (var index = Math.Max(0, startIndex); index < messages.Count; index++)
        {
            var message = messages[index];
            var role = message.Role ?? "user";

            if (role == "system")
            {
                var text = NormalizeContent(message.Content);
                if (!string.IsNullOrEmpty(text)) system.Append(text).Append('\n');
            }
            else if (role == "user")
            {
                var text = NormalizeContent(message.Content);
                if (!string.IsNullOrEmpty(text)) conversation.Append("User: ").Append(text).Append("\n\n");
            }
            else if (role == "assistant")
            {
                if (message.ToolCalls is { Count: > 0 })
                {
                    foreach (var call in message.ToolCalls)
                    {
                        conversation.Append("Assistant: TOOL_CALL: ").Append(call.Function?.Name)
                            .Append("\narguments: ").Append(call.Function?.Arguments).Append("\n\n");
                    }
                }
                else
                {
                    var text = NormalizeContent(message.Content);
                    if (!string.IsNullOrEmpty(text)) conversation.Append("Assistant: ").Append(text).Append("\n\n");
                }
            }
            else if (role == "tool")
            {
                var text = NormalizeContent(message.Content);
                if (!string.IsNullOrEmpty(text)) conversation.Append("[Tool Result]\n").Append(text).Append("\n\n");
            }
        }

        return (conversation.ToString().Trim(), system.ToString().Trim());
    }
}
