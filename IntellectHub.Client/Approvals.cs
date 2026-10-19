namespace IntellectHub.Client;

/// <summary>
/// How much of the agent's work runs without stopping to ask.
/// </summary>
public enum Approvals
{
    /// <summary>Stop before every tool call.</summary>
    Ask,

    /// <summary>Run tools that only read; stop before anything that can change the project.</summary>
    Writes,

    /// <summary>Never stop. The agent runs whatever it asks for.</summary>
    All,
}

/// <summary>
/// Where a tool call stands relative to the barrier. Every tool the client can
/// run is classified here rather than inline, so a tool added later cannot
/// accidentally be treated as safe.
/// </summary>
public static class ToolRisk
{
    /// <summary>
    /// Tools that only observe the workspace. Safe to run unattended: they cannot
    /// change a file, and the model is no more able to act on them by running one
    /// than by asking.
    /// </summary>
    private static readonly HashSet<string> ReadOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "read", "glob", "grep", "check",
    };

    /// <summary>
    /// Anything that writes a file or runs a shell command. These are what the
    /// barrier exists for.
    /// </summary>
    public static bool CanWrite(string tool) => !ReadOnly.Contains(tool);

    /// <summary>True when the current mode lets this call proceed unattended.</summary>
    public static bool Allowed(Approvals mode, string tool) => mode switch
    {
        Approvals.All => true,
        Approvals.Writes => !CanWrite(tool),
        _ => false,
    };

    public static Approvals Parse(string? value) =>
        Enum.TryParse<Approvals>(value?.Trim(), ignoreCase: true, out var mode) ? mode : Approvals.Ask;

    /// <summary>
    /// The spellings a user is likely to reach for, folded onto the stored name.
    /// Without this a "yes" would parse as neither mode and silently land on Ask,
    /// which is the one answer nobody wants to get by accident.
    /// </summary>
    public static string Normalize(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Equals("y", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("all", StringComparison.OrdinalIgnoreCase))
            return nameof(Approvals.All);

        if (trimmed.Equals("w", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("writes", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("write", StringComparison.OrdinalIgnoreCase))
            return nameof(Approvals.Writes);

        return nameof(Approvals.Ask);
    }

    public static string Describe(Approvals mode) => mode switch
    {
        Approvals.All => "everything runs automatically, nothing is asked",
        Approvals.Writes => "read-only tools run automatically, writes and commands are asked",
        _ => "every tool call is asked before it runs",
    };
}