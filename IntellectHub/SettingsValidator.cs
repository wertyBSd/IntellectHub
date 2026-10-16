namespace IntellectHub;

/// <summary>One thing wrong with one field, with text fit to show next to it.</summary>
internal sealed record FieldProblem(string Field, string Message)
{
    public override string ToString() => Message;
}

/// <summary>
/// Validates the settings form before anything is written.
///
/// Clamping in <see cref="AppConfig.Normalize"/> protects the gateway, but silently
/// rewriting what someone typed is how you end up wondering why the number in the
/// window is not the number in use.
/// </summary>
internal static class SettingsValidator
{
    public static IReadOnlyList<FieldProblem> Validate(AppConfig config)
    {
        var problems = new List<FieldProblem>();

        if (string.IsNullOrWhiteSpace(config.Host))
            problems.Add(new(nameof(config.Host), "Хост не может быть пустым"));
        else if (config.Host.Contains(' '))
            problems.Add(new(nameof(config.Host), "В хосте не должно быть пробелов"));
        else if (config.Host is not ("127.0.0.1" or "localhost" or "0.0.0.0") &&
                 !System.Net.IPAddress.TryParse(config.Host, out _) && !IsPlausibleName(config.Host))
            problems.Add(new(nameof(config.Host), "Не похоже на адрес: нужен IP или имя хоста"));

        if (config.Port is < 1 or > 65535)
            problems.Add(new(nameof(config.Port), "Порт должен быть от 1 до 65535"));

        if (config.MinRequestIntervalMs is < 0 or > 600_000)
            problems.Add(new(nameof(config.MinRequestIntervalMs), "Интервал должен быть от 0 до 600000 мс"));

        if (config.MaxConcurrent is < 1 or > 512)
            problems.Add(new(nameof(config.MaxConcurrent), "От 1 до 512 одновременных запросов"));

        if (config.AccountCooldownSeconds is < 1 or > 86_400)
            problems.Add(new(nameof(config.AccountCooldownSeconds), "Пауза от 1 суток до 1 года"));

        if (config.UpstreamTimeoutSeconds is < 5 or > 600)
            problems.Add(new(nameof(config.UpstreamTimeoutSeconds), "Таймаут от 5 до 600 секунд"));

        if (config.RequestDeadlineSeconds < config.UpstreamTimeoutSeconds)
            problems.Add(new(nameof(config.RequestDeadlineSeconds),
                "Дедлайн должен быть не меньше таймаута одного запроса"));

        if (config.LockWaitSeconds > config.RequestDeadlineSeconds)
            problems.Add(new(nameof(config.LockWaitSeconds),
                "Ожидание аккаунта не может превышать общий дедлайн"));

        if (config.MaxPromptChars < AppConfig.MinPromptChars)
            problems.Add(new(nameof(config.MaxPromptChars),
                $"Лимит промпта от {AppConfig.MinPromptChars} символов"));

        if (config.MaxRetries is < 0 or > 10)
            problems.Add(new(nameof(config.MaxRetries), "От 0 до 10 повторов"));

        return problems;
    }

    private static bool IsPlausibleName(string host) =>
        host.Length <= 255 &&
        host.Split('.').All(label => label.Length is > 0 and <= 63) &&
        !host.Any(char.IsWhiteSpace);
}