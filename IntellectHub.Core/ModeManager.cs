using System.Linq;
using System.Text.Json.Nodes;

namespace IntellectHub.Client;

/// <summary>
/// Управляет режимами разработки: выбирает активный режим для каждого хода,
/// переключает их по порядку (Архитектор → Код → Отладка → Код …),
/// записывает историю и артефакты в .IntellectHub и подготавливает контекст для модели.
///
/// Есть два режима. <see cref="Current"/> — режим, выбранный пользователем: он виден
/// в интерфейсе и хранится в mode.json. <see cref="Effective"/> — рабочий режим,
/// который определяет, какие инструкции получает модель. Пока пользователь в Auto,
/// переходы происходят только в Effective: видимый режим остаётся Auto, а объявления
/// о переходах не сыплются. Явный выбор режима (/mode …) меняет оба.
/// </summary>
public sealed class ModeManager
{
    private readonly Project _project;
    private readonly ModeStore _store;
    private readonly IModeNotifier? _notifier;
    private ModeState _state;
    private Mode _effective;
    private string _turnTask = "";
    private string _originalUserTask = "";

    /// <summary>Режим, выбранный пользователем. Виден в интерфейсе и хранится в mode.json.</summary>
    public Mode Current => _state.Current;

    /// <summary>Внутренний рабочий режим. В Auto меняется под капотом, не трогая Current.</summary>
    public Mode Effective => _effective;

    public ModeInfo CurrentInfo => Modes.Of(_state.Current);
    public ModeInfo EffectiveInfo => Modes.Of(_effective);
    public string CurrentDisplay => _state.Current.ToString().ToLowerInvariant();
    public string EffectiveDisplay => _effective.ToString().ToLowerInvariant();
    public ModeState State => _state;

    public ModeManager(Project project, ModeStore store, IModeNotifier? notifier = null)
    {
        _project = project;
        _store = store;
        _notifier = notifier;
        _state = store.Load();
        _effective = _state.Current;
        NotifyModeChanged();
        Announce($"режим работы: {CurrentInfo.Title} — {CurrentInfo.Description}");
    }

    public void Persist() => _store.Save(_state);

    /// <summary>Выбирает активный режим для нового хода пользователя.</summary>
    public void StartTurn(string userInput)
    {
        _turnTask = userInput;

        // Запоминаем исходную задачу пользователя один раз — при первом входе в задачу.
        if (string.IsNullOrEmpty(_originalUserTask) && userInput.Trim().Length > 0)
            _originalUserTask = userInput;

        // Всегда фиксируем LastTask.
        _state.LastTask = _originalUserTask;

        // Обновляем обнаруженное состояние проекта из .IntellectHub.
        _state.DetectedState = _store.DetectProjectState();
        Persist();

        var mode = DecideMode(userInput);

        SetEffective(mode, $"новая задача: {Short(_originalUserTask)}");
    }

    /// <summary>Принудительный переход к следующему режиму по порядку после завершения этапа.</summary>
    public void AdvanceToNextMode()
    {
        var next = NextModeAfter(_effective);
        if (next is null)
        {
            Announce("Все этапы пройдены — проект готов.");
            return;
        }
        SetEffective(next.Value, $"завершён этап {_effective}, переходим к {next.Value}");
    }

    /// <summary>Завершить часть текущего мода — клиент переходит к следующему по порядку.</summary>
    public void Complete(Mode mode)
    {
        switch (mode)
        {
            case Mode.Architect:
                _state.ArchitectureReady = true;
                _state.PlanReady = true;
                SetEffective(Mode.Code, "архитектура и план готовы — переходим к коду");
                break;
            case Mode.Code:
                _state.CodeReady = true;
                _state.LastError = null;
                Announce("код готов по плану. если появятся ошибки — включим отладку.");
                break;
            case Mode.Debug:
                _state.DebugCycles++;
                _state.LastError = null;
                SetEffective(Mode.Code, "исправление внесено — проверяем в режиме кода");
                break;
        }
        Persist();
    }

    /// <summary>Автоматический переход к следующему режиму, если текущий этап завершён по флагам.</summary>
    public void AdvanceIfReady()
    {
        switch (_effective)
        {
            case Mode.Architect:
                if (_state.ArchitectureReady && _state.PlanReady)
                    SetEffective(Mode.Code, "архитектура и план готовы — переходим к коду");
                break;
            case Mode.Code:
                if (_state.LastError != null && _state.DebugCycles < 3)
                    SetEffective(Mode.Debug, "ошибка в коде — переход к отладке");
                break;
            case Mode.Debug:
                _state.LastError = null;
                SetEffective(Mode.Code, "отладка завершена — проверяем в режиме кода");
                break;
        }
    }

    /// <summary>Пользователь явно выбрал режим: он становится и видимым, и рабочим.</summary>
    public void SwitchTo(Mode mode, string reason)
    {
        var from = _state.Current;
        _state.Current = mode;
        _effective = mode;
        // Используем исходную задачу пользователя, а не ответ на вопрос Auto Mode ("1" / "2").
        var taskForHistory = string.IsNullOrEmpty(_originalUserTask) ? _turnTask : _originalUserTask;
        _state.LastTask = taskForHistory;
        _state.History.Add(new ModeEntry { From = from, To = mode, Reason = reason, Task = taskForHistory });
        Persist();
        NotifyModeChanged();
        Announce($"переход мода: {from} → {mode} · {reason}");
    }

    /// <summary>
    /// Внутренний переход. Пока пользователь в Auto, режим меняется только под капотом:
    /// видимый <see cref="Current"/>, история и объявления остаются нетронутыми, а модель
    /// продолжает получать инструкции рабочего режима через <see cref="EffectiveInfo"/>.
    /// </summary>
    private void SetEffective(Mode mode, string reason)
    {
        if (_state.Current == Mode.Auto)
        {
            // Видимый режим остаётся Auto, но внутренний прогресс фиксируем в истории:
            // она живёт в mode.json и даёт модели контекст о том, что уже пройдено.
            var from = _effective;
            if (from != mode)
            {
                _effective = mode;
                var taskForHistory = string.IsNullOrEmpty(_originalUserTask) ? _turnTask : _originalUserTask;
                _state.History.Add(new ModeEntry { From = from, To = mode, Reason = reason, Task = taskForHistory });
            }
            Persist();
            return;
        }

        // Явно выбранный режим не меняется сам собой: лишний переход в истории ни к чему.
        if (mode == _state.Current)
        {
            _effective = mode;
            return;
        }

        SwitchTo(mode, reason);
    }

    /// <summary>Записать артефакт мода в .IntellectHub и обновить флаги готовности.</summary>
    public void RecordArtifact(Mode mode, string file, string content)
    {
        // Пустой или уходящий из проекта путь — не артефакт: инструмент сам
        // вернёт модели внятную ошибку, а запись в никуда только потеряет ход.
        string resolved;
        try { resolved = _store.ResolveArtifactPath(file); }
        catch (InvalidOperationException) { return; }

        _store.WriteArtifact(resolved, content);
        var lower = resolved.ToLowerInvariant();

        if (mode == Mode.Architect)
        {
            if (lower.EndsWith("architecture.md")) _state.ArchitectureReady = true;
            if (lower.EndsWith("plan.md")) _state.PlanReady = true;
        }
        else if (mode == Mode.Code || mode == Mode.Debug)
        {
            // Любой записанный файл вне .IntellectHub и docs — это код проекта.
            if (!lower.Contains(".intellecthub") && !lower.Contains("/docs/") && !lower.Contains("\\docs\\"))
                _state.CodeReady = true;
        }

        Persist();
    }

    private Mode DecideMode(string input)
    {
        var lower = input.ToLowerInvariant();

        // Явный выбор режима пользователем.
        foreach (var info in Modes.All)
            if (lower.Contains(info.Name) || lower.Contains(info.Title.ToLowerInvariant()))
                return info.Mode;

        // Auto Mode: определяем состояние проекта и выбираем режим автоматически.
        // Клиент сам принимает решение, не спрашивает пользователя.
        if (_state.Current == Mode.Auto)
        {
            switch (_state.DetectedState)
            {
                case ProjectState.Empty:
                    // Пустой проект — начинаем с архитектуры без вопроса пользователю.
                    return Mode.Architect;

                case ProjectState.HasArchitecture:
                    return Mode.Architect;

                case ProjectState.HasPlan:
                    return Mode.Code;

                case ProjectState.HasCode:
                    return _state.LastError != null && _state.DebugCycles < 3
                        ? Mode.Debug
                        : Mode.Code;

                case ProjectState.HasDebug:
                    return Mode.Code;
            }
        }

        // Ручная последовательность по порядку (когда Auto не выбран).
        if (_state.History.Count == 0) return Mode.Orchestrator;
        if (!_state.ArchitectureReady) return Mode.Architect;
        if (!_state.PlanReady) return Mode.Architect;
        if (!_state.CodeReady) return Mode.Code;
        if (_state.LastError != null && _state.DebugCycles < 3) return Mode.Debug;
        return _effective;
    }

    /// <summary>Зафиксировать ошибку инструмента: если мы в режиме кода — уйти в отладку.</summary>
    public void NoteError(string tool, string output)
    {
        _state.LastError = $"{tool}: {Trim(output, 300)}";
        if (_effective == Mode.Code || _effective == Mode.Debug)
        {
            _state.DebugCycles++;
            SetEffective(Mode.Debug, $"ошибка при выполнении {tool}");
        }
        Persist();
    }

    public void ClearError()
    {
        _state.LastError = null;
        Persist();
    }

    /// <summary>Сброс флагов для новой задачи — начинаем с архитектора.</summary>
    public void ResetForNewTask()
    {
        var from = _state.Current;
        _state.Current = Mode.Auto;
        _effective = Mode.Auto;
        _state.ArchitectureReady = false;
        _state.PlanReady = false;
        _state.CodeReady = false;
        _state.LastError = null;
        _state.DebugCycles = 0;
        _state.History.Add(new ModeEntry { From = from, To = Mode.Auto, Reason = "сброс для новой задачи", Task = _turnTask });
        Persist();
        NotifyModeChanged();
        Announce("моды сброшены — следующая задача начнётся с архитектора (Architect Mode).");
    }

    /// <summary>Содержимое артефактов .IntellectHub для контекста модели.</summary>
    public string MemoryContext()
    {
        var parts = new List<string>();
        var arch = _store.Read(_store.ArchitecturePath);
        if (arch.Length > 0) parts.Add("## Архитектура (.IntellectHub/architecture.md)\n" + Trim(arch, 2000));
        var plan = _store.Read(_store.PlanPath);
        if (plan.Length > 0) parts.Add("## План (.IntellectHub/plan.md)\n" + Trim(plan, 2000));
        var debug = _store.Read(_store.DebugLogPath);
        if (debug.Length > 0) parts.Add("## Лог отладки (.IntellectHub/debug-log.md)\n" + Trim(debug, 1500));
        if (parts.Count == 0) return "";
        return "### Память проекта (.IntellectHub)\n\n" + string.Join("\n\n", parts) + "\n\n";
    }

    /// <summary>История чата с добавленным системным напоминанием о текущем моде и памяти проекта.</summary>
    public IReadOnlyList<ChatMessage> WithContext(IReadOnlyList<ChatMessage> history)
    {
        var list = new List<ChatMessage>();
        var head = ModeInstruction() + MemoryContext();
        if (head.Length > 0)
            list.Add(new ChatMessage { Role = "user", Content = JsonValue.Create(head) });
        foreach (var m in history) list.Add(m);
        return list;
    }

    private string ModeInstruction()
    {
        var info = EffectiveInfo;
        var next = NextModeAfter(_effective);
        var nextText = next is null ? "завершение задачи" : Modes.Of(next.Value).Title;
        return $"[активный режим: {info.Title} ({info.Name})]\n{info.SystemPrompt}\nДальше по порядку: {nextText}.\nИстория переходов: {FormatHistory()}\n";
    }

    private Mode? NextModeAfter(Mode mode) => mode switch
    {
        Mode.Orchestrator => Mode.Architect,
        Mode.Architect => Mode.Code,
        Mode.Code => _state.LastError != null ? Mode.Debug : null,
        Mode.Debug => Mode.Code,
        _ => null
    };

    private string FormatHistory()
    {
        var recent = _state.History.TakeLast(4);
        if (!recent.Any()) return "переходов ещё нет";
        return string.Join(" → ", recent.Select(e => e.To.ToString()));
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string Short(string value) => Trim(value.Replace("\r\n", " ").Replace('\n', ' '), 80);

    private void NotifyModeChanged()
    {
        _notifier?.OnModeChanged(CurrentDisplay);
    }

    private void Announce(string text)
    {
        _notifier?.OnAnnounce(text);
    }
}
