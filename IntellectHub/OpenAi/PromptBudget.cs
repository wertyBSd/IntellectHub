namespace IntellectHub;

/// <summary>
/// Fits the flattened prompt into the character budget without destroying the
/// parts that make tool calling work.
/// </summary>
internal static class PromptBudget
{
    /// <summary>Left where history was dropped, so the model can see a seam.</summary>
    public const string Marker = "\n\n[Earlier context compacted by IntellectHub]\n\n";

    public const string Separator = "\n\n";

    /// <summary>
    /// Joins the parts that must survive truncation (system prompt, tool manifest,
    /// tool reminder) ahead of the conversation, and spends what is left on the
    /// conversation. A manifest cut in half is worse than a lost turn: it silently
    /// removes the model's ability to call any tool at all.
    /// </summary>
    public static string Build(string? systemPrompt, string conversation, IReadOnlyList<string?> reserved, int budget)
    {
        var head = Join(reserved);

        var reservedCost = head.Length + (head.Length > 0 ? Separator.Length : 0);
        var room = budget - reservedCost;
        if (room <= 0)
        {
            // Even the reserved parts alone overflow. Trim the oldest history
            // rather than the manifest, and let the caller's cap handle the rest.
            return TruncateMiddle(head, budget);
        }

        var body = conversation.Length <= room ? conversation : TruncateMiddle(conversation, room);

        if (systemPrompt is { Length: > 0 }) head = Join([systemPrompt, head]);

        return body.Length > 0 ? Join([head, body]) : head;
    }

    public static string Join(IReadOnlyList<string?> parts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            if (sb.Length > 0) sb.Append(Separator);
            sb.Append(part);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Keeps the head and the tail, dropping the middle. In a flattened transcript
    /// the head holds the instructions and the tail the turns that led here, so
    /// the middle is the only safe place to cut.
    /// </summary>
    public static string TruncateMiddle(string text, int budget)
    {
        if (text.Length <= budget) return text;
        if (budget <= Marker.Length) return text[..Math.Max(0, budget)];

        var room = budget - Marker.Length;
        var head = room / 2;
        var tail = room - head;

        return text[..head] + Marker + text[^tail..];
    }
}