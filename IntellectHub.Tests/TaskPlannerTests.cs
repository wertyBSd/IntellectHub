using System.Text.Json;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Тесты TaskPlanner — планировщика подзадач.
/// Проверяет: загрузку JSON-плана, последовательное выполнение,
/// отметку выполненных/проваленных подзадач, повтор при ошибках.
/// </summary>
public sealed class TaskPlannerTests
{
    private static string PlanJson(int count = 3)
    {
        var items = new List<string>();
        for (var i = 1; i <= count; i++)
            items.Add($"{{\"title\":\"Подзадача {i}\",\"description\":\"Описание {i}\"}}");
        return $"{{\"subtasks\":[{string.Join(",", items)}]}}";
    }

    [Fact]
    public void Load_From_Plan_Json_Parses_Subtasks()
    {
        var planner = new IntellectHub.Client.TaskPlanner();
        planner.LoadFromPlan(PlanJson(3));

        Assert.Equal(3, planner.Total);
        Assert.Equal(3, planner.Subtasks.Count);
        Assert.Equal("Подзадача 1", planner.Subtasks[0].Title);
        Assert.Equal("Описание 1", planner.Subtasks[0].Description);
        Assert.Equal(IntellectHub.Client.SubtaskStatus.Pending, planner.Subtasks[0].Status);
        Assert.True(planner.HasNext);
        Assert.Equal(planner.Subtasks[0], planner.Current);
    }

    [Fact]
    public void Load_From_Empty_Plan_Returns_Zero()
    {
        var planner = new IntellectHub.Client.TaskPlanner();
        planner.LoadFromPlan("{\"subtasks\":[]}");

        Assert.Equal(0, planner.Total);
        Assert.False(planner.HasNext);
        Assert.Null(planner.Current);
    }

    [Fact]
    public void Load_From_Array_Without_Subtasks_Key()
    {
        // Некоторые модели возвращают просто массив, а не {"subtasks": [...]}
        var planner = new IntellectHub.Client.TaskPlanner();
        planner.LoadFromPlan("[{\"title\":\"Задача 1\",\"description\":\"Описание\"}]");

        Assert.Equal(1, planner.Total);
        Assert.Equal("Задача 1", planner.Subtasks[0].Title);
    }

    [Fact]
    public void Mark_Current_Done_Advances_To_Next()
    {
        var planner = new IntellectHub.Client.TaskPlanner();
        planner.LoadFromPlan(PlanJson(3));

        planner.MarkCurrentDone("результат 1");
        Assert.Equal(IntellectHub.Client.SubtaskStatus.Done, planner.Subtasks[0].Status);
        Assert.Equal("результат 1", planner.Subtasks[0].Result);
        Assert.Equal("Подзадача 2", planner.Current?.Title);
        Assert.True(planner.HasNext);

        planner.MarkCurrentDone("результат 2");
        planner.MarkCurrentDone("результат 3");
        Assert.False(planner.HasNext);
        Assert.Null(planner.Current);
    }

    [Fact]
    public void Mark_Current_Failed_Advances_To_Next()
    {
        var planner = new IntellectHub.Client.TaskPlanner();
        planner.LoadFromPlan(PlanJson(2));

        planner.MarkCurrentFailed("ошибка");
        Assert.Equal(IntellectHub.Client.SubtaskStatus.Failed, planner.Subtasks[0].Status);
        Assert.Equal("ошибка", planner.Subtasks[0].Error);
        Assert.Equal("Подзадача 2", planner.Current?.Title);
        Assert.True(planner.HasNext);
    }

    [Fact]
    public void Plan_Summary_Shows_Status()
    {
        var planner = new IntellectHub.Client.TaskPlanner();
        planner.LoadFromPlan(PlanJson(2));
        planner.MarkCurrentDone("готово");

        var summary = planner.PlanSummary();
        Assert.Contains("✓", summary);
        Assert.Contains("1.", summary);
        Assert.Contains("2.", summary);
        Assert.Contains("Подзадача 1", summary);
        Assert.Contains("Подзадача 2", summary);
    }

    [Fact]
    public void Subtask_Tracks_Attempts()
    {
        var planner = new IntellectHub.Client.TaskPlanner();
        planner.LoadFromPlan(PlanJson(1));

        // Симулируем неудачные попытки
        var subtask = planner.Current!;
        subtask.Attempts++;
        subtask.Attempts++;
        Assert.Equal(2, subtask.Attempts);
    }
}
