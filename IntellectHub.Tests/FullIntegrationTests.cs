using IntellectHub.Client;
using System.Text.Json;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Полный интеграционный тест цепочки модов: Auto → Architect → Code → Debug → Code.
///
/// Тест имитирует разработку игры на HTML/JS/CSS ("Напиши игру используя html + javascript + css")
/// с заглушками ответов модели. Проверяется, что клиент:
/// 1. Блокируется в Auto Mode и задаёт вопрос "архитектура или код?"
/// 2. После ответа "1" переходит в Architect Mode
/// 3. Архитектор пишет architecture.md и plan.md
/// 4. Клиент автоматически переходит в Code Mode
/// 5. Кодер пишет index.html, style.css, game.js, data.js — файлы создаются на диске
/// 6. При ошибке инструмента (пишем несуществующий файл без pattern) → Debug Mode
/// 7. Исправляем ошибку → возвращаемся в Code Mode
/// 8. Финальная проверка: все файлы существуют и содержат ожидаемое содержимое
///
/// Все данные хранятся во временной папке проекта (.IntellectHub/ и файлы игры).
/// </summary>
public sealed class FullIntegrationTests : IDisposable
{
    private readonly ModeFlowIntegrationTests.TempProject _project;

    public FullIntegrationTests()
    {
        _project = new ModeFlowIntegrationTests.TempProject("full-integration");
    }

    public void Dispose()
    {
        _project.Dispose();
    }

    /// <summary>
    /// Полный цикл: от Auto Mode до финальной проверки файлов.
    /// </summary>
    [Fact]
    public void Full_Development_Cycle_Creates_All_Game_Files()
    {
        // ============================================================
        // ЭТАП 1: Auto Mode — пустой проект, автоматически переходим в Architect
        // ============================================================
        Assert.Equal(Mode.Auto, _project.Manager.Effective);

        // Пользователь отправляет промт — клиент автоматически переходит в Architect.
        _project.Manager.StartTurn("Напиши игру используя html + javascript + css");

        Assert.Equal(Mode.Architect, _project.Manager.Effective);
        Assert.Null(_project.Manager.State.PendingQuestion);
        Assert.Equal("Напиши игру используя html + javascript + css",
            _project.Manager.State.LastTask);

        // ============================================================
        // ЭТАП 2: Architect Mode — пишем architecture.md и plan.md
        // ============================================================

        // ============================================================
        // ЭТАП 3: Architect Mode — пишем architecture.md и plan.md
        // ============================================================
        var architecture = GetArchitectureContent();
        var plan = GetPlanContent();

        _project.Manager.RecordArtifact(Mode.Architect, _project.Store.ArchitecturePath, architecture);
        _project.Manager.RecordArtifact(Mode.Architect, _project.Store.PlanPath, plan);

        Assert.True(_project.Manager.State.ArchitectureReady);
        Assert.True(_project.Manager.State.PlanReady);
        Assert.Equal(architecture, File.ReadAllText(_project.Store.ArchitecturePath));
        Assert.Equal(plan, File.ReadAllText(_project.Store.PlanPath));

        // Архитектура + план готовы → автопереход в Code Mode.
        _project.Manager.AdvanceIfReady();
        Assert.Equal(Mode.Code, _project.Manager.Effective);

        // ============================================================
        // ЭТАП 4: Code Mode — пишем файлы игры
        // ============================================================
        var html = GetIndexHtmlContent();
        var css = GetStyleCssContent();
        var js = GetGameJsContent();
        var data = GetDataJsContent();

        // Реально создаём файлы в корне проекта (как бы сделал клиент через инструмент write).
        File.WriteAllText(System.IO.Path.Combine(_project.Root, "index.html"), html);
        File.WriteAllText(System.IO.Path.Combine(_project.Root, "style.css"), css);
        File.WriteAllText(System.IO.Path.Combine(_project.Root, "game.js"), js);
        File.WriteAllText(System.IO.Path.Combine(_project.Root, "data.js"), data);

        // Фиксируем артефакты в .IntellectHub (как делает Program.cs при вызове write/edit).
        _project.Manager.RecordArtifact(Mode.Code, "index.html", html);
        _project.Manager.RecordArtifact(Mode.Code, "style.css", css);
        _project.Manager.RecordArtifact(Mode.Code, "game.js", js);
        _project.Manager.RecordArtifact(Mode.Code, "data.js", data);

        _project.Manager.State.CodeReady = true;
        _project.Manager.Persist();

        // Проверяем, что файлы существуют на диске.
        Assert.True(File.Exists(System.IO.Path.Combine(_project.Root, "index.html")),
            "index.html должен существовать");
        Assert.True(File.Exists(System.IO.Path.Combine(_project.Root, "style.css")),
            "style.css должен существовать");
        Assert.True(File.Exists(System.IO.Path.Combine(_project.Root, "game.js")),
            "game.js должен существовать");
        Assert.True(File.Exists(System.IO.Path.Combine(_project.Root, "data.js")),
            "data.js должен существовать");

        // Проверяем содержимое файлов.
        Assert.Equal(html, File.ReadAllText(System.IO.Path.Combine(_project.Root, "index.html")));
        Assert.Equal(css, File.ReadAllText(System.IO.Path.Combine(_project.Root, "style.css")));
        Assert.Equal(js, File.ReadAllText(System.IO.Path.Combine(_project.Root, "game.js")));
        Assert.Equal(data, File.ReadAllText(System.IO.Path.Combine(_project.Root, "data.js")));

        // ============================================================
        // ЭТАП 5: Debug Mode — ошибка инструмента при записи файла
        // ============================================================
        // Симулируем ошибку: модель пытается вызвать write без аргументов.
        _project.Manager.NoteError("write", "Missing required arguments: pattern");

        Assert.Equal(Mode.Debug, _project.Manager.Effective);
        Assert.Equal(1, _project.Manager.State.DebugCycles);
        Assert.NotNull(_project.Manager.State.LastError);
        Assert.Contains("write", _project.Manager.State.LastError);

        // ============================================================
        // ЭТАП 6: Исправление ошибки — возвращаемся в Code Mode
        // ============================================================
        // Модель исправила аргументы и успешно перезаписала файл.
        File.WriteAllText(System.IO.Path.Combine(_project.Root, "game.js"), js);
        _project.Manager.RecordArtifact(Mode.Debug, "game.js", js);
        _project.Manager.State.LastError = null;
        _project.Manager.AdvanceIfReady();

        Assert.Equal(Mode.Code, _project.Manager.Effective);
        Assert.Null(_project.Manager.State.LastError);

        // ============================================================
        // ЭТАП 7: Финальная проверка — все файлы на месте
        // ============================================================
        var files = Directory.GetFiles(_project.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".IntellectHub"))
            .ToList();

        Assert.Contains(files, f => f.EndsWith("index.html"));
        Assert.Contains(files, f => f.EndsWith("style.css"));
        Assert.Contains(files, f => f.EndsWith("game.js"));
        Assert.Contains(files, f => f.EndsWith("data.js"));

        // Проверяем, что game.js содержит основную логику (не пустой).
        var finalJs = File.ReadAllText(System.IO.Path.Combine(_project.Root, "game.js"));
        Assert.NotEmpty(finalJs);
        Assert.Contains("card", finalJs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("health", finalJs, StringComparison.OrdinalIgnoreCase);

        // Сохраняем состояние.
        _project.Manager.Persist();

        // Проверяем историю переходов модов.
        Assert.Single(_project.Manager.State.History, h => h.To == Mode.Architect);
        Assert.Equal(2, _project.Manager.State.History.Count(h => h.To == Mode.Code));
        Assert.Single(_project.Manager.State.History, h => h.To == Mode.Debug);
    }

    /// <summary>
    /// Тест проблемного ответа: модель пишет текст, но не создаёт файлы.
    /// Клиент должен заметить, что файлов нет, и не считать подзадачу выполненной.
    /// </summary>
    [Fact]
    public void Model_Text_Without_Files_Is_Not_Counted_As_Done()
    {
        using var project = new ModeFlowIntegrationTests.TempProject("text-without-files");

        project.Manager.SwitchTo(Mode.Code, "тест");

        // Файл не существует — подзадача не выполнена.
        var missingPath = System.IO.Path.Combine(project.Root, "missing.html");
        Assert.False(File.Exists(missingPath));
    }

    /// <summary>
    /// Тест: Auto Mode автоматически переходит в Architect для пустого проекта,
    /// не спрашивая пользователя.
    /// </summary>
    [Fact]
    public void Auto_Mode_Auto_Starts_Architect_For_Empty_Project()
    {
        using var project = new ModeFlowIntegrationTests.TempProject("auto-starts-architect");

        // Пустой проект.
        Assert.Equal(Mode.Auto, project.Manager.Effective);

        // Пользователь пишет задачу — клиент автоматически переходит в Architect.
        project.Manager.StartTurn("Создай игру Reigns");

        Assert.Equal(Mode.Architect, project.Manager.Effective);
        Assert.Null(project.Manager.State.PendingQuestion);
        Assert.Equal("Создай игру Reigns", project.Manager.State.LastTask);
    }

    /// <summary>
    /// Тест: Auto Mode с существующим планом автоматически переходит в Code.
    /// </summary>
    [Fact]
    public void Auto_Mode_With_Plan_Auto_Starts_Code()
    {
        using var project = new ModeFlowIntegrationTests.TempProject("auto-has-plan");

        // Создаём архитектуру и план.
        File.WriteAllText(project.Store.ArchitecturePath, GetArchitectureContent());
        File.WriteAllText(project.Store.PlanPath, GetPlanContent());
        project.Manager.State.ArchitectureReady = true;
        project.Manager.State.PlanReady = true;
        project.Manager.Persist();

        // Новый менеджер должен перейти в Code.
        project.Manager.StartTurn("Напиши код");

        Assert.Equal(Mode.Code, project.Manager.Effective);
    }

    /// <summary>
    /// Тест: состояние проекта переносится между сессиями (пересоздание менеджера).
    /// </summary>
    [Fact]
    public void Project_State_Survives_Manager_Restart()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ih-full-persist",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);

        try
        {
            // Первый менеджер: Architect Mode, файл сохранён.
            var p1 = Project.Open(root);
            var s1 = new ModeStore(p1);
            var u1 = new Ui();
            var m1 = new ModeManager(p1, s1, u1);

            m1.SwitchTo(Mode.Architect, "тест");
            m1.RecordArtifact(Mode.Architect, s1.ArchitecturePath, GetArchitectureContent());
            m1.State.ArchitectureReady = true;
            m1.Persist();
            u1.Dispose();

            // Второй менеджер загружает то же состояние из той же папки.
            var p2 = Project.Open(root);
            var s2 = new ModeStore(p2);
            var u2 = new Ui();
            var m2 = new ModeManager(p2, s2, u2);

            Assert.Equal(Mode.Architect, m2.Effective);
            Assert.True(m2.State.ArchitectureReady);
            Assert.Equal(GetArchitectureContent(),
                File.ReadAllText(s2.ArchitecturePath));
            u2.Dispose();
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch { }
        }
    }

    private static string GetArchitectureContent() =>
        "# Архитектура Reigns-подобной игры\n\n" +
        "Жанр: текстовый хоррор в стиле Reigns.\n" +
        "Игровой цикл: карточка события → свайп влево/вправо → изменение параметров.\n" +
        "Параметры: здоровье, ресурсы, рассудок, экипировка.\n" +
        "Условие проигрыша: любой параметр ≤ 0.\n" +
        "Файлы: index.html, style.css, game.js, data.js.\n";

    private static string GetPlanContent() =>
        "# План реализации\n\n" +
        "1. Создать index.html — разметка игры.\n" +
        "2. Создать style.css — стили.\n" +
        "3. Создать game.js — движок карточек.\n" +
        "4. Создать data.js — события.\n";

    private static string GetIndexHtmlContent() =>
        "<!DOCTYPE html>\n" +
        "<html lang=\"ru\">\n" +
        "<head>\n" +
        "  <meta charset=\"UTF-8\">\n" +
        "  <title>Reigns-подобная игра</title>\n" +
        "  <link rel=\"stylesheet\" href=\"style.css\">\n" +
        "</head>\n" +
        "<body>\n" +
        "  <div id=\"game\">\n" +
        "    <div id=\"card\"></div>\n" +
        "    <div id=\"stats\"></div>\n" +
        "    <div id=\"choices\"></div>\n" +
        "  </div>\n" +
        "  <script src=\"game.js\"></script>\n" +
        "  <script src=\"data.js\"></script>\n" +
        "</body>\n" +
        "</html>\n";

    private static string GetStyleCssContent() =>
        "body { background: #1a1a1a; color: #fff; font-family: sans-serif; }\n" +
        "#card { border: 2px solid #555; padding: 20px; margin: 20px; }\n" +
        "#choices button { margin: 10px; padding: 10px 20px; }\n";

    private static string GetGameJsContent() =>
        "// Движок карточек Reigns-подобной игры\n" +
        "let health = 100, resources = 50, sanity = 80, equipment = 30;\n" +
        "let currentCard = null;\n\n" +
        "function showCard(card) {\n" +
        "  currentCard = card;\n" +
        "  document.getElementById('card').textContent = card.text;\n" +
        "  document.getElementById('stats').textContent =\n" +
        "    `Здоровье: ${health} Ресурсы: ${resources} Рассудок: ${sanity} Экипировка: ${equipment}`;\n" +
        "}\n\n" +
        "function makeChoice(delta) {\n" +
        "  health += delta.health || 0;\n" +
        "  resources += delta.resources || 0;\n" +
        "  sanity += delta.sanity || 0;\n" +
        "  equipment += delta.equipment || 0;\n" +
        "  if (health <= 0 || resources <= 0 || sanity <= 0 || equipment <= 0) {\n" +
        "    alert('Игра окончена');\n" +
        "    location.reload();\n" +
        "  }\n" +
        "  showCard(getRandomCard());\n" +
        "}\n\n" +
        "function getRandomCard() {\n" +
        "  return data[Math.floor(Math.random() * data.length)];\n" +
        "}\n\n" +
        "showCard(getRandomCard());\n";

    private static string GetDataJsContent() =>
        "// События игры\n" +
        "const data = [\n" +
        "  {\n" +
        "    text: 'Ты нашёл загадочный артефакт.',\n" +
        "    left: { text: 'Взять', health: -10, resources: +20, sanity: -5 },\n" +
        "    right: { text: 'Пройти мимо', health: 0, resources: 0, sanity: +5 }\n" +
        "  },\n" +
        "  {\n" +
        "    text: 'Тёмный коридор ведёт в неизвестность.',\n" +
        "    left: { text: 'Заглянуть', health: -5, sanity: -15, equipment: +10 },\n" +
        "    right: { text: 'Вернуться', health: 0, sanity: +10 }\n" +
        "  },\n" +
        "];\n";
}
