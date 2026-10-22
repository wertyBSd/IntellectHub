using System.Text.Json;

namespace IntellectHub.Client;

/// <summary>
/// Планировщик подзадач. Модель при сложном запросе возвращает JSON-план со
/// списком подзадач. Клиент выполняет их по очереди: запрашивает у модели
/// выполнение очередной подзадачи, ждёт результата (вызов инструментов) и
/// переходит к следующей.
/// </summary>
public sealed class TaskPlanner
{
    private readonly List<Subtask> _subtasks = new();
    private int _currentIndex = 0;

    public IReadOnlyList<Subtask> Subtasks => _subtasks;

    public int Total => _subtasks.Count;

    /// <summary>Загружает план из JSON-ответа модели.</summary>
    public void LoadFromPlan(string planJson)
    {
        using var doc = JsonDocument.Parse(planJson);
        var root = doc.RootElement;

        JsonElement arr;
        if (root.ValueKind == JsonValueKind.Array)
        {
            // Модель вернула просто массив подзадач
            arr = root;
        }
        else if (root.TryGetProperty("subtasks", out var subtasks))
        {
            arr = subtasks;
        }
        else
        {
            arr = default;
        }

        _subtasks.Clear();
        _currentIndex = 0;
        var i = 0;

        if (arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                _subtasks.Add(new Subtask
                {
                    Index = i++,
                    Title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    Status = SubtaskStatus.Pending
                });
            }
        }
    }

    public Subtask? Current => _currentIndex < _subtasks.Count ? _subtasks[_currentIndex] : null;

    public bool HasNext => _currentIndex < _subtasks.Count;

    public void MarkCurrentDone(string result)
    {
        if (_currentIndex < _subtasks.Count)
        {
            var s = _subtasks[_currentIndex];
            s.Status = SubtaskStatus.Done;
            s.Result = result;
            _currentIndex++;
        }
    }

    public void MarkCurrentFailed(string error)
    {
        if (_currentIndex < _subtasks.Count)
        {
            var s = _subtasks[_currentIndex];
            s.Status = SubtaskStatus.Failed;
            s.Error = error;
            _currentIndex++;
        }
    }

    public string PlanSummary() =>
        string.Join("\n", _subtasks.Select(s =>
            $"  [{(s.Status == SubtaskStatus.Done ? "✓" : s.Status == SubtaskStatus.Failed ? "✗" : "·")}] " +
            $"{s.Index + 1,2}. {s.Title}"));
}
