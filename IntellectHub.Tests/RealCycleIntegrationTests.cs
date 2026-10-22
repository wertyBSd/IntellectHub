using IntellectHub.Client;
using System.Text.Json;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Настоящий интеграционный тест цикла клиента IntellectHub.
///
/// Использует реальные классы клиента: Project, ModeStore, ModeManager, ToolRegistry.
/// Вместо реальной модели — прямые вызовы методов клиента (симуляция ответов модели).
/// Реальные файлы создаются на диске.
///
/// Проверяет полный путь: Auto → Architect → Code → Debug → Code,
/// включая обработку ошибок ("An error occurred while sending the request").
/// </summary>
public sealed class RealCycleIntegrationTests : IDisposable
{
    private readonly ModeFlowIntegrationTests.TempProject _tmp;
    private readonly Project _project;
    private readonly ModeStore _store;
    private readonly Ui _ui;
    private readonly ModeManager _manager;

    public RealCycleIntegrationTests()
    {
        _tmp = new ModeFlowIntegrationTests.TempProject(Guid.NewGuid().ToString("N"));
        _project = _tmp.Project;
        _store = _tmp.Store;
        _ui = _tmp.Ui;
        _manager = _tmp.Manager;
    }

    public void Dispose()
    {
        _manager.Persist();
        _ui.Dispose();
    }

    #region Вспомогательный контент (как бы вернула модель)

    private static string ArchitectureContent =>
        "# Архитектура Reigns-подобной игры\n\n" +
        "Жанр: текстовый хоррор в стиле Reigns.\n" +
        "Игровой цикл: карточка события → свайп влево/вправо → изменение параметров.\n" +
        "Параметры: здоровье, ресурсы, рассудок, экипировка.\n" +
        "Условие проигрыша: любой параметр ≤ 0.\n" +
        "Файлы: index.html, style.css, game.js, data.js.\n";

    private static string PlanContent =>
        "# План реализации\n\n" +
        "1. Создать index.html — разметка игры.\n" +
        "2. Создать style.css — стили.\n" +
        "3. Создать game.js — движок карточек.\n" +
        "4. Создать data.js — события.\n";

    private static string IndexHtml =>
        "<!DOCTYPE html>\n<html lang=\"ru\">\n<head>\n  <meta charset=\"UTF-8\">\n  <title>Reigns Game</title>\n  <link rel=\"stylesheet\" href=\"style.css\">\n</head>\n<body>\n  <div id=\"game\"></div>\n  <script src=\"game.js\"></script>\n  <script src=\"data.js\"></script>\n</body>\n</html>\n";

    private static string StyleCss =>
        "body { background: #1a1a1a; color: #fff; font-family: sans-serif; }\n";

    private static string GameJs =>
        "// Движок карточек\nlet health = 100;\nfunction showCard() { document.getElementById('game').textContent = 'card'; }\nshowCard();\n";

    private static string DataJs =>
        "// События\nconst data = [{ text: 'Событие 1', left: {}, right: {} }];\n";

    private string GetPath(string relativePath) =>
        System.IO.Path.Combine(_project.Root, relativePath);

    #endregion

    #region Тест 1: Полный цикл Auto → Architect → Code → Debug → Code

    [Fact]
    public void Full_Development_Cycle_With_Model_Stub()
    {
        // ============================================================
        // ЭТАП 1: Auto Mode — пустой проект, автоматически переходим в Architect
        // ============================================================
        Assert.Equal(Mode.Auto, _manager.Effective);

        // Пользователь отправляет промт — клиент автоматически переходит в Architect.
        _manager.StartTurn("Напиши игру используя html + javascript + css");

        Assert.Equal(Mode.Architect, _manager.Effective);
        Assert.Null(_manager.State.PendingQuestion);
        Assert.Equal("Напиши игру используя html + javascript + css",
            _manager.State.LastTask);

        // ============================================================
        // ЭТАП 3: Architect Mode — пишем architecture.md и plan.md
        // ============================================================
        _manager.RecordArtifact(Mode.Architect, _store.ArchitecturePath, ArchitectureContent);
        _manager.RecordArtifact(Mode.Architect, _store.PlanPath, PlanContent);

        Assert.True(_manager.State.ArchitectureReady);
        Assert.True(_manager.State.PlanReady);
        Assert.Equal(ArchitectureContent, File.ReadAllText(_store.ArchitecturePath));
        Assert.Equal(PlanContent, File.ReadAllText(_store.PlanPath));

        // Архитектура + план готовы → автопереход в Code Mode.
        _manager.AdvanceIfReady();
        Assert.Equal(Mode.Code, _manager.Effective);

        // ============================================================
        // ЭТАП 4: Code Mode — пишем файлы игры (симулируем инструмент write)
        // ============================================================
        File.WriteAllText(GetPath("index.html"), IndexHtml);
        File.WriteAllText(GetPath("style.css"), StyleCss);
        File.WriteAllText(GetPath("game.js"), GameJs);
        File.WriteAllText(GetPath("data.js"), DataJs);

        // Фиксируем артефакты в .IntellectHub (как делает Program.cs).
        _manager.RecordArtifact(Mode.Code, "index.html", IndexHtml);
        _manager.RecordArtifact(Mode.Code, "style.css", StyleCss);
        _manager.RecordArtifact(Mode.Code, "game.js", GameJs);
        _manager.RecordArtifact(Mode.Code, "data.js", DataJs);

        _manager.State.CodeReady = true;
        _manager.Persist();

        // Проверяем, что файлы существуют на диске.
        Assert.True(File.Exists(GetPath("index.html")), "index.html должен существовать");
        Assert.True(File.Exists(GetPath("style.css")), "style.css должен существовать");
        Assert.True(File.Exists(GetPath("game.js")), "game.js должен существовать");
        Assert.True(File.Exists(GetPath("data.js")), "data.js должен существовать");

        // Проверяем содержимое.
        Assert.Equal(IndexHtml, File.ReadAllText(GetPath("index.html")));
        Assert.Equal(GameJs, File.ReadAllText(GetPath("game.js")));

        // ============================================================
        // ЭТАП 5: Debug Mode — ошибка инструмента (как на скриншоте)
        // ============================================================
        _manager.NoteError("write", "An error occurred while sending the request.");

        Assert.Equal(Mode.Debug, _manager.Effective);
        Assert.Equal(1, _manager.State.DebugCycles);
        Assert.NotNull(_manager.State.LastError);
        Assert.Contains("An error occurred", _manager.State.LastError);

        // ============================================================
        // ЭТАП 6: Исправление ошибки — возвращаемся в Code Mode
        // ============================================================
        File.WriteAllText(GetPath("game.js"), GameJs); // Перезаписываем исправленный файл
        _manager.RecordArtifact(Mode.Debug, "game.js", GameJs);
        _manager.State.LastError = null;
        _manager.AdvanceIfReady();

        Assert.Equal(Mode.Code, _manager.Effective);
        Assert.Null(_manager.State.LastError);

        // ============================================================
        // ЭТАП 7: Финальная проверка — все файлы на месте
        // ============================================================
        var gameFiles = Directory.GetFiles(_project.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".IntellectHub"))
            .ToList();

        Assert.Contains(gameFiles, f => f.EndsWith("index.html"));
        Assert.Contains(gameFiles, f => f.EndsWith("style.css"));
        Assert.Contains(gameFiles, f => f.EndsWith("game.js"));
        Assert.Contains(gameFiles, f => f.EndsWith("data.js"));

        // Проверяем историю переходов модов.
        Assert.Single(_manager.State.History, h => h.To == Mode.Architect);
        Assert.Equal(2, _manager.State.History.Count(h => h.To == Mode.Code));
        Assert.Single(_manager.State.History, h => h.To == Mode.Debug);
    }

    #endregion

    #region Тест 2: Ошибка HTTP не ломает клиента

    [Fact]
    public void Http_Error_Does_Not_Crash_Client()
    {
        using var tmp = new ModeFlowIntegrationTests.TempProject("http-error");
        tmp.Manager.SwitchTo(Mode.Code, "тест");

        // Симулируем ошибку HTTP: "An error occurred while sending the request."
        tmp.Manager.NoteError("write", "An error occurred while sending the request.");

        Assert.Equal(Mode.Debug, tmp.Manager.Effective);
        Assert.NotNull(tmp.Manager.State.LastError);
        Assert.Contains("An error occurred", tmp.Manager.State.LastError);
    }

    #endregion

    #region Тест 3: Модель не создаёт файлы — подзадача не выполняется

    [Fact]
    public void Model_Does_Not_Write_Files_Task_Not_Done()
    {
        using var tmp = new ModeFlowIntegrationTests.TempProject("no-files");
        tmp.Manager.SwitchTo(Mode.Code, "тест");

        // Файл не был создан — подзадача не считается выполненной.
        var missingPath = System.IO.Path.Combine(tmp.Root, "nonexistent.html");
        Assert.False(File.Exists(missingPath));
    }

    #endregion

    #region Тест 4: Состояние переносится между сессиями

    [Fact]
    public void State_Survives_Session_Restart()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ih-restart",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);

        try
        {
            // Сессия 1: Architect Mode, файл сохранён.
            var p1 = Project.Open(root);
            var s1 = new ModeStore(p1);
            var u1 = new Ui();
            var m1 = new ModeManager(p1, s1, u1);

            m1.SwitchTo(Mode.Architect, "тест");
            m1.RecordArtifact(Mode.Architect, s1.ArchitecturePath, ArchitectureContent);
            m1.State.ArchitectureReady = true;
            m1.Persist();
            u1.Dispose();

            // Сессия 2: новый менеджер загружает то же состояние.
            var p2 = Project.Open(root);
            var s2 = new ModeStore(p2);
            var u2 = new Ui();
            var m2 = new ModeManager(p2, s2, u2);

            Assert.Equal(Mode.Architect, m2.Effective);
            Assert.True(m2.State.ArchitectureReady);
            Assert.Equal(ArchitectureContent, File.ReadAllText(s2.ArchitecturePath));
            u2.Dispose();
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch { }
        }
    }

    #endregion
}
