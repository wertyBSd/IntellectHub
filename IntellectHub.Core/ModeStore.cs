using System.Linq;
using System.Text.Json;

namespace IntellectHub.Client;

/// <summary>
/// Хранилище данных модов проекта внутри его папки .IntellectHub:
/// mode.json — текущий режим и история переходов,
/// architecture.md — архитектура (Architect),
/// plan.md — план реализации (Architect/Orchestrator),
/// debug-log.md — анализ ошибок (Debug),
/// orchestrator.md — заметки координатора (Orchestrator).
/// </summary>
public sealed class ModeStore
{
    private readonly Project _project;
    private readonly string _statePath;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string HubDir => _project.HubDir;
    public string StatePath => _statePath;
    public string ArchitecturePath => Path.Combine(HubDir, "architecture.md");
    public string PlanPath => Path.Combine(HubDir, "plan.md");
    public string DebugLogPath => Path.Combine(HubDir, "debug-log.md");
    public string OrchestratorPath => Path.Combine(HubDir, "orchestrator.md");

    public ModeStore(Project project)
    {
        _project = project;
        _statePath = Path.Combine(HubDir, "mode.json");
        Directory.CreateDirectory(HubDir);
    }

    /// <summary>Определяет, что уже есть в .IntellectHub этого проекта.</summary>
    public ProjectState DetectProjectState()
    {
        var hasArch = File.Exists(ArchitecturePath) && new FileInfo(ArchitecturePath).Length > 0;
        var hasPlan = File.Exists(PlanPath) && new FileInfo(PlanPath).Length > 0;

        // Код считаем наличием хотя бы одного файла игры вне .IntellectHub и docs.
        var workspaceFiles = Directory.GetFiles(_project.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine(_project.Root, ".IntellectHub")))
            .Where(f => !f.Contains(Path.Combine(_project.Root, "docs")))
            .Where(f => Path.GetExtension(f) is ".html" or ".css" or ".js" or ".ts" or ".json")
            .ToList();
        var hasCode = workspaceFiles.Count > 0;

        var hasDebug = File.Exists(DebugLogPath) && new FileInfo(DebugLogPath).Length > 0;

        if (!hasArch && !hasPlan && !hasDebug && !hasCode) return ProjectState.Empty;
        if (hasDebug) return ProjectState.HasDebug;
        if (hasCode) return ProjectState.HasCode;
        if (hasPlan) return ProjectState.HasPlan;
        if (hasArch) return ProjectState.HasArchitecture;
        return ProjectState.Empty;
    }

    public ModeState Load()
    {
        if (!File.Exists(_statePath)) return new ModeState();
        try
        {
            var state = JsonSerializer.Deserialize<ModeState>(File.ReadAllText(_statePath), Json);
            return state ?? new ModeState();
        }
        catch (JsonException)
        {
            // Повреждённый файл состояния не должен лечь клиент — начнём с чистого состояния.
            return new ModeState();
        }
    }

    public void Save(ModeState state)
    {
        state.UpdatedAt = DateTime.UtcNow;
        File.WriteAllText(_statePath, JsonSerializer.Serialize(state, Json));
    }

    /// <summary>Сохранить план реализации в .IntellectHub/plan.md и отметить флаг PlanReady.</summary>
    public void SavePlan(string planMd, ModeState state)
    {
        File.WriteAllText(PlanPath, planMd);
        state.PlanReady = true;
        Save(state);
    }

    /// <summary>Очистить отложенный вопрос Auto Mode.</summary>
    public void ClearPendingQuestion(ModeState state)
    {
        state.PendingQuestion = null;
        Save(state);
    }

    /// <summary>Прочитать содержимое файла, если существует.</summary>
    public string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    /// <summary>
    /// Maps an artifact path onto the project.
    ///
    /// A leading separator means "the project root", not the drive root: models
    /// write <c>/index.html</c> the way they write a URL, and Path.Combine treats
    /// a leading separator as "discard everything before it". A genuinely
    /// absolute path outside the project is refused, exactly as the write tool
    /// refuses it, so recording an artifact can never reach outside the workspace.
    /// </summary>
    public string ResolveArtifactPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("path is empty");

        var relative = path.Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (relative.Length == 0)
            throw new InvalidOperationException("path names no file");

        var full = Path.GetFullPath(Path.Combine(_project.Root, relative));

        // Compared against the root plus a separator, so a sibling directory whose
        // name merely starts with the project name is not mistaken for one inside.
        var root = _project.Root.EndsWith(Path.DirectorySeparatorChar)
            ? _project.Root
            : _project.Root + Path.DirectorySeparatorChar;

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path escapes workspace: {path}");

        return full;
    }

    /// <summary>Записать артефакт мода (создаёт каталог при необходимости).</summary>
    public void WriteArtifact(string path, string text)
    {
        var full = ResolveArtifactPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, text);
    }

    /// <summary>Прочитать артефакт проекта, если он существует внутри рабочей папки.</summary>
    public bool TryReadArtifact(string path, out string text)
    {
        text = "";
        string full;
        try { full = ResolveArtifactPath(path); }
        catch (InvalidOperationException) { return false; }

        if (!File.Exists(full)) return false;
        try { text = File.ReadAllText(full); return true; }
        catch (IOException) { return false; }
    }
}
