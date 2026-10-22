using System.Text.Json.Serialization;

namespace IntellectHub.Client;

/// <summary>Одна запись о переходе между модами в истории проекта.</summary>
public sealed class ModeEntry
{
    [JsonPropertyName("at")] public DateTime At { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("from")] public Mode? From { get; set; }
    [JsonPropertyName("to")] public Mode To { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("task")] public string? Task { get; set; }
}

/// <summary>Что клиент обнаружил в папке .IntellectHub проекта.</summary>
public enum ProjectState
{
    /// <summary>Папка .IntellectHub пуста — первый запуск по этому проекту.</summary>
    Empty,

    /// <summary>Есть архитектура (.IntellectHub/architecture.md).</summary>
    HasArchitecture,

    /// <summary>Есть архитектура и план (.IntellectHub/plan.md).</summary>
    HasPlan,

    /// <summary>Есть архитектура, план и уже написан код.</summary>
    HasCode,

    /// <summary>Есть логи отладки (.IntellectHub/debug-log.md).</summary>
    HasDebug,
}

/// <summary>
/// Состояние модов одного проекта. Живёт в .IntellectHub/mode.json и переносится
/// из сессии в сессию, чтобы разработка шла по порядку и не теряла контекст.
/// </summary>
public sealed class ModeState
{
    [JsonPropertyName("current")] public Mode Current { get; set; } = Mode.Auto;
    [JsonPropertyName("lastTask")] public string? LastTask { get; set; }
    [JsonPropertyName("history")] public List<ModeEntry> History { get; set; } = new();
    [JsonPropertyName("architectureReady")] public bool ArchitectureReady { get; set; }
    [JsonPropertyName("planReady")] public bool PlanReady { get; set; }
    [JsonPropertyName("codeReady")] public bool CodeReady { get; set; }
    [JsonPropertyName("debugCycles")] public int DebugCycles { get; set; }
    [JsonPropertyName("lastError")] public string? LastError { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Последнее обнаруженное состояние проекта .IntellectHub.</summary>
    [JsonPropertyName("detectedState")] public ProjectState DetectedState { get; set; } = ProjectState.Empty;

    /// <summary>Вопрос, который клиент задал пользователю и ждёт ответа (Auto Mode).</summary>
    [JsonPropertyName("pendingQuestion")] public string? PendingQuestion { get; set; }
}
