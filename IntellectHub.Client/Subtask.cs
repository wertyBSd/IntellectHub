namespace IntellectHub.Client;

/// <summary>Одна подзадача в плане выполнения сложной задачи.</summary>
public sealed class Subtask
{
    public int Index { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public SubtaskStatus Status { get; set; } = SubtaskStatus.Pending;
    public string? Result { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; } = 0;
}

public enum SubtaskStatus
{
    Pending,
    InProgress,
    Done,
    Failed
}
