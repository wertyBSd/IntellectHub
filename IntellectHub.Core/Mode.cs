using System.Text.Json.Serialization;

namespace IntellectHub.Client;

/// <summary>
/// Режимы работы клиента. Новая задача проходит через них по порядку:
/// Orchestrator → Architect → Code → (при ошибках) Debug → Code …
/// Все данные модов проекта хранятся в его папке .IntellectHub.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Mode
{
    Auto,
    Orchestrator,
    Architect,
    Code,
    Debug
}

/// <summary>Описание одного мода: название, инструкция для модели и файл в .IntellectHub,
/// в котором этот режим хранит результат своей работы.</summary>
public sealed record ModeInfo(
    Mode Mode,
    string Name,
    string Title,
    string Description,
    string HubFile,
    string SystemPrompt
);

public static class Modes
{
    public static IReadOnlyList<ModeInfo> All { get; }

    static Modes()
    {
        All = new List<ModeInfo>
        {
            new(
                Mode.Auto,
                "auto",
                "Авто",
                "Автоматически определяет состояние проекта и выбирает нужный режим.",
                "mode.json",
                "Ты — режим Авто. Ты НЕ пишешь код и НЕ проектируешь сам сам. Твоя единственная задача на этом этапе — " +
                "оценить состояние проекта (.IntellectHub) и спросить пользователя, с чего начать. " +
                "Если в .IntellectHub ничего нет — ОБЯЗАН спросить: «Создать архитектуру или посмотреть существующий код?» " +
                "и ДОЖДАТЬСЯ ответа. Не принимай решения вместо пользователя, не начинай писать код и не komponуй архитектуру " +
                "без явного согласия. Если архитектура и план уже есть в .IntellectHub — переходи к коду. " +
                "Если код уже есть и есть ошибки — переходи к отладке."),

            new(
                Mode.Orchestrator,
                "orchestrator",
                "Оркестратор",
                "Разбирает сложную задачу на шаги и координирует остальные режимы.",
                "orchestrator.md",
                "Ты — режим Оркестратор. Ты не пишешь код сам, а координируешь разработку: " +
                "разбиваешь задачу пользователя на последовательность шагов, определяешь, " +
                "какой режим делает каждый шаг (Архитектор → Код → Отладка → Код …) и " +
                "записывай план в .IntellectHub/plan.md. Когда план готов, передай управление режиму Архитектор."),

            new(
                Mode.Architect,
                "architect",
                "Архитектор",
                "Проектирует архитектуру приложения и строит план до любого кода.",
                "architecture.md",
                "Ты — режим Архитектор. Код писать пока нельзя: сначала архитектура и план." +
                " Опиши модули, зависимости, стек и точки входа, составь пошаговый план реализации, " +
                "запиши архитектуру в .IntellectHub/architecture.md, план — в .IntellectHub/plan.md, " +
                "и сообщи, когда они готовы. Клиент затем перейдёт в режим Код."),

            new(
                Mode.Code,
                "code",
                "Код",
                "Непосредственное написание и изменение кода.",
                "plan.md",
                "Ты — режим Код. Архитектура и план уже утверждены (.IntellectHub/architecture.md, " +
                ".IntellectHub/plan.md). Пиши и меняй код только через инструменты write/edit — " +
                "не выводи содержимое файлов текстом в ответе. Следуй плану по порядку. " +
                "Если сборка/тест/запуск завершился ошибкой — не гадай, а переведи клиента " +
                "в режим Отладка (Debug Mode)."),

            new(
                Mode.Debug,
                "debug",
                "Отладка",
                "Поиск багов, анализ логов и исправление ошибок.",
                "debug-log.md",
                "Ты — режим Отладка. Код работает с ошибкой. Прочитай вывод ошибки и соответствующие " +
                "файлы (read), запиши анализ в .IntellectHub/debug-log.md (что сломано, почему, " +
                "какое исправление), исправь код через edit/write, затем верни клиента в режим Код для проверки.")
        };
    }

    public static ModeInfo Of(Mode mode) => All.First(i => i.Mode == mode);

    public static Mode Parse(string value)
    {
        foreach (var info in All)
            if (string.Equals(info.Name, value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(info.Title, value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(info.Mode.ToString(), value, StringComparison.OrdinalIgnoreCase))
                return info.Mode;
        throw new ArgumentException(
            $"неизвестный режим '{value}'. Доступно: {string.Join(", ", All.Select(i => i.Name))}");
    }
}
