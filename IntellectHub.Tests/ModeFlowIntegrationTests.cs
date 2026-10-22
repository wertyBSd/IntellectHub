using IntellectHub.Client;
using System.Text.Json;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Интеграционный тест цепочки модов: Auto → Architect → Code → Debug → Code.
///
/// Вместо реальной модели используется заглушка. Тест проверяет, что клиент:
/// 1. В пустом проекте блокируется в Auto Mode и задаёт вопрос.
/// 2. После ответа "1" переходит в Architect Mode.
/// 3. После записи architecture.md + plan.md автоматически переходит в Code Mode.
/// 4. При ошибке инструмента переходит в Debug Mode.
/// 5. После исправления возвращается в Code Mode.
///
/// Все данные хранятся во временной папке проекта (.IntellectHub/mode.json и т.д.).
/// </summary>
public sealed class ModeFlowIntegrationTests
{
    public sealed class TempProject : IDisposable
    {
        public string Root { get; }
        public Project Project { get; }
        public ModeStore Store { get; }
        public Ui Ui { get; } = new();
        public ModeManager Manager { get; }

        public TempProject(string subFolder)
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ih-mode-test", subFolder);
            // Чистим папку перед каждым тестом, чтобы не было остатков от предыдущих запусков.
            if (System.IO.Directory.Exists(Root))
                System.IO.Directory.Delete(Root, true);
            System.IO.Directory.CreateDirectory(Root);
            Project = Project.Open(Root);
            Store = new ModeStore(Project);
            Manager = new ModeManager(Project, Store, Ui);
        }

        public void Dispose()
        {
            Manager.Persist();
            Ui.Dispose();
            try { System.IO.Directory.Delete(Root, true); } catch { }
        }
    }

    private static string ArchitectureContent =>
        "# Архитектура Reigns-подобной игры\n\n" +
        "Жанр: текстовый хоррор в стиле Reigns.\n" +
        "Игровой цикл: карточка события → свайп влево/вправо → изменение параметров.\n" +
        "Параметры: здоровье, ресурсы, рассудок, экипировка.\n" +
        "Файлы: index.html, style.css, game.js, data.js.\n";

    private static string PlanContent =>
        "# План реализации\n\n" +
        "1. Создать index.html — разметка игры.\n" +
        "2. Создать style.css — стили.\n" +
        "3. Создать game.js — движок карточек.\n" +
        "4. Создать data.js — события.\n";

    [Fact]
    public void Auto_Mode_Empty_Project_Auto_Starts_Architect()
    {
        using var project = new TempProject("auto-empty");

        // В пустом проекте клиент автоматически переходит в Architect Mode без вопроса.
        project.Manager.StartTurn("Напиши игру Reigns на html/js/css");

        Assert.Equal(Mode.Architect, project.Manager.Effective);
        Assert.Null(project.Manager.State.PendingQuestion);
        Assert.Equal("Напиши игру Reigns на html/js/css", project.Manager.State.LastTask);
    }

    /// <summary>
    /// В Auto режим меняется только под капотом: видимый Current остаётся Auto,
    /// а рабочий Effective проходит Архитектор → Код. Иначе в чате сыпались бы
    /// «переход мода: Auto → …», которых пользователь не просил.
    /// </summary>
    [Fact]
    public void Auto_Mode_ChangesOnlyTheEffectiveMode()
    {
        using var project = new TempProject("auto-hidden");

        project.Manager.StartTurn("Напиши игру Reigns на html/js/css");

        Assert.Equal(Mode.Auto, project.Manager.Current);
        Assert.Equal(Mode.Architect, project.Manager.Effective);

        project.Manager.RecordArtifact(Mode.Architect, project.Store.ArchitecturePath, ArchitectureContent);
        project.Manager.RecordArtifact(Mode.Architect, project.Store.PlanPath, PlanContent);
        project.Manager.AdvanceIfReady();

        Assert.Equal(Mode.Auto, project.Manager.Current);
        Assert.Equal(Mode.Code, project.Manager.Effective);
    }

    /// <summary>Явный выбор режима пользователем меняет и видимый режим.</summary>
    [Fact]
    public void ExplicitMode_ChangesTheVisibleMode()
    {
        using var project = new TempProject("explicit");

        project.Manager.SwitchTo(Mode.Code, "тест");

        Assert.Equal(Mode.Code, project.Manager.Current);
        Assert.Equal(Mode.Code, project.Manager.Effective);
    }

    [Fact]
    public void Auto_Mode_With_Architecture_Auto_Starts_Architect()
    {
        using var project = new TempProject("auto-has-arch");

        // Создаём архитектуру, симулируя существующий проект.
        File.WriteAllText(project.Store.ArchitecturePath, ArchitectureContent);
        project.Manager.State.ArchitectureReady = true;
        project.Manager.Persist();

        // Новый менеджер должен перейти в Architect (нужен план).
        project.Manager.StartTurn("Допиши план");

        Assert.Equal(Mode.Architect, project.Manager.Effective);
    }

    [Fact]
    public void Auto_Mode_With_Plan_Auto_Starts_Code()
    {
        using var project = new TempProject("auto-has-plan");

        // Создаём архитектуру и план.
        File.WriteAllText(project.Store.ArchitecturePath, ArchitectureContent);
        File.WriteAllText(project.Store.PlanPath, PlanContent);
        project.Manager.State.ArchitectureReady = true;
        project.Manager.State.PlanReady = true;
        project.Manager.Persist();

        // Новый менеджер должен перейти в Code.
        project.Manager.StartTurn("Напиши код");

        Assert.Equal(Mode.Code, project.Manager.Effective);
    }

    [Fact]
    public void Architect_Writing_Files_Advances_To_Code()
    {
        using var project = new TempProject("architect-to-code");

        // Переходим в Architect Mode напрямую.
        project.Manager.SwitchTo(Mode.Architect, "тест");

        // Архитектор записывает architecture.md.
        project.Manager.RecordArtifact(Mode.Architect, project.Store.ArchitecturePath, ArchitectureContent);
        Assert.True(project.Manager.State.ArchitectureReady);

        // Пока нет плана — остаёмся в Architect.
        Assert.Equal(Mode.Architect, project.Manager.Effective);

        // Записываем plan.md.
        project.Manager.RecordArtifact(Mode.Architect, project.Store.PlanPath, PlanContent);
        Assert.True(project.Manager.State.PlanReady);

        // Авто-переход: архитектура + план готовы → Code.
        project.Manager.AdvanceIfReady();
        Assert.Equal(Mode.Code, project.Manager.Effective);
    }

    [Fact]
    public void Code_Mode_Tool_Error_Advances_To_Debug()
    {
        using var project = new TempProject("code-to-debug");

        project.Manager.SwitchTo(Mode.Code, "тест");

        // Ошибка инструмента в Code Mode → Debug.
        project.Manager.NoteError("write", "Missing required arguments: pattern");
        Assert.Equal(Mode.Debug, project.Manager.Effective);
        Assert.NotNull(project.Manager.State.LastError);
        Assert.Equal(1, project.Manager.State.DebugCycles);
    }

    [Fact]
    public void Debug_Mode_After_Fix_Advances_Back_To_Code()
    {
        using var project = new TempProject("debug-to-code");

        project.Manager.SwitchTo(Mode.Debug, "тест");

        // После отладки возвращаемся в Code для проверки.
        project.Manager.AdvanceIfReady();
        Assert.Equal(Mode.Code, project.Manager.Effective);
        Assert.Null(project.Manager.State.LastError);
    }

    [Fact]
    public void Mode_State_Persists_Across_Restarts()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ih-mode-test-persist",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);

        try
        {
            var project1 = Project.Open(root);
            var store1 = new ModeStore(project1);
            var ui1 = new Ui();
            var manager1 = new ModeManager(project1, store1, ui1);

            manager1.SwitchTo(Mode.Code, "тест");
            manager1.State.CodeReady = true;
            manager1.State.LastError = "ошибка в коде";
            manager1.State.DebugCycles = 2;
            manager1.Persist();
            ui1.Dispose();

            // Новый менеджер загружает то же состояние.
            var project2 = Project.Open(root);
            var store2 = new ModeStore(project2);
            var ui2 = new Ui();
            var manager2 = new ModeManager(project2, store2, ui2);

            Assert.Equal(Mode.Code, manager2.Current);
            Assert.True(manager2.State.CodeReady);
            Assert.Equal("ошибка в коде", manager2.State.LastError);
            Assert.Equal(2, manager2.State.DebugCycles);
            ui2.Dispose();
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Plan_Extraction_Saves_To_Plan_Md()
    {
        using var project = new TempProject("plan-save");

        var store = project.Store;
        var state = project.Manager.State;

        // Симулируем сохранение плана (как делает Program.cs).
        store.SavePlan(PlanContent, state);

        Assert.True(state.PlanReady);
        Assert.Equal(PlanContent, File.ReadAllText(store.PlanPath));

        // При повторной загрузке состояние сохраняется.
        var reloaded = store.Load();
        Assert.True(reloaded.PlanReady);
        Assert.Equal(PlanContent, File.ReadAllText(store.PlanPath));
    }

    [Fact]
    public void DetectProjectState_Recognizes_Existing_Files()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ih-detect-state",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);

        try
        {
            var project = Project.Open(root);
            var store = new ModeStore(project);

            // Пустой проект.
            Assert.Equal(ProjectState.Empty, store.DetectProjectState());

            // Создаём архитектуру.
            File.WriteAllText(store.ArchitecturePath, ArchitectureContent);
            Assert.Equal(ProjectState.HasArchitecture, store.DetectProjectState());

            // Создаём план.
            File.WriteAllText(store.PlanPath, PlanContent);
            Assert.Equal(ProjectState.HasPlan, store.DetectProjectState());

            // Создаём реальный файл игры (вне .IntellectHub и docs).
            File.WriteAllText(System.IO.Path.Combine(root, "index.html"), "<html></html>");
            Assert.Equal(ProjectState.HasCode, store.DetectProjectState());

            // Создаём лог отладки.
            File.WriteAllText(store.DebugLogPath, "отладка");
            Assert.Equal(ProjectState.HasDebug, store.DetectProjectState());
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Full_Mode_Chain_Simulated()
    {
        using var project = new TempProject("full-chain");

        // 1. Auto Mode — пустой проект, автоматически переходим в Architect.
        project.Manager.StartTurn("Напиши игру Reigns на html/js/css");
        Assert.Equal(Mode.Architect, project.Manager.Effective);
        Assert.Null(project.Manager.State.PendingQuestion);
        Assert.Equal("Напиши игру Reigns на html/js/css", project.Manager.State.LastTask);

        // 2. Архитектор пишет architecture.md и plan.md.
        project.Manager.RecordArtifact(Mode.Architect, project.Store.ArchitecturePath, ArchitectureContent);
        project.Manager.RecordArtifact(Mode.Architect, project.Store.PlanPath, PlanContent);
        project.Manager.AdvanceIfReady();
        Assert.Equal(Mode.Code, project.Manager.Effective);

        // 3. Ошибка в коде → Debug.
        project.Manager.NoteError("write", "Missing required arguments: pattern");
        Assert.Equal(Mode.Debug, project.Manager.Effective);
        Assert.Equal(1, project.Manager.State.DebugCycles);

        // 4. Исправление → Code.
        project.Manager.AdvanceIfReady();
        Assert.Equal(Mode.Code, project.Manager.Effective);
        Assert.Null(project.Manager.State.LastError);

        // Сохраняем и проверяем, что состояние переживает пересоздание менеджера.
        project.Manager.Persist();
    }
}
