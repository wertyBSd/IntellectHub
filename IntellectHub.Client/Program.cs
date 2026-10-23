using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using IntellectHub.Client;

var config = ClientConfig.Load();

if (args.Length > 0 && args[0] is "--version" or "-v")
{
    Console.WriteLine($"IntellectHub.Client {Banner.Version}");
    return;
}

if (args.Length > 0 && args[0] is "--help" or "-h")
{
    Console.WriteLine("IntellectHub Console - a Claude-style terminal client");
    Console.WriteLine("usage: IntellectHub.Client [--url <server>] [--model <name>] [--project <path>] [--yes|--approve ask|writes|all]");
    Console.WriteLine("       inside the client: F1 help, Ctrl+K commands, Ctrl+O settings, Shift+Tab approvals");
    Console.WriteLine($"version: {Banner.Version}");
    Console.WriteLine($"config: {ClientConfig.ConfigPath}");
    return;
}

for (var i = 0; i + 1 < args.Length; i++)
{
    switch (args[i])
    {
        case "--url" or "-u": config.OverrideBaseUrl(args[i + 1]); break;
        case "--model" or "-m": config.OverrideModel(args[i + 1]); break;
        case "--project" or "-p": config.Project = args[i + 1]; break;

        // The short flag people reach for first, and the explicit form for a
        // value that says which of the two middle modes is meant.
        case "--yes" or "-y": config.ApprovalMode = nameof(Approvals.All); break;
        case "--approve" or "--approvals": config.ApprovalMode = ToolRisk.Normalize(args[i + 1]); break;

        // --server picks one of the saved gateways by name; --url stays available
        // for an address that was never saved.
        case "--server" or "-s":
            var named = config.Servers.FirstOrDefault(x =>
                string.Equals(x.Name, args[i + 1], StringComparison.OrdinalIgnoreCase));
            if (named is not null)
            {
                config.OverrideBaseUrl(named.Url);
                config.ServerName = named.Name;
            }
            else
            {
                config.OverrideBaseUrl(args[i + 1]);
                config.ServerName = "";
            }
            break;
    }
}

if (args.Length > 0 && args[0] == "--tool")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: IntellectHub.Client --tool <name> [json-args] [--project <path>]");
        return;
    }

    var toolName = args[1];
    var toolArgs = args.Length > 2 && !args[2].StartsWith('-') ? args[2] : "{}";
    var registry = new ToolRegistry(ResolveProjectRoot(), config);
    var outcome = await registry.ExecuteAsync(new ToolCall { Name = toolName, Arguments = toolArgs });
    Console.WriteLine(outcome.Output);
    Environment.ExitCode = outcome.IsError ? 1 : 0;
    return;
}

using var screen = new Ui();
var api = new ApiClient(config, GetActiveConnection());
var history = new List<ChatMessage>();

// Identity of this conversation for the gateway.
var sessionKey = NewSessionKey();

var project = Project.Open(ResolveProjectRoot());
var tools = new ToolRegistry(project.Root, config);
var log = new SessionLog(project.LogPath);
ModeStore? _modeStore = null;
ModeManager? _modes = null;
_modeStore = new ModeStore(project);
_modes = new ModeManager(project, _modeStore, screen);
RememberProject(project.Root);

// Планировщик подзадач: когда модель возвращает JSON-план со списком подзадач,
// клиент выполняет их по очереди, один за другим.
TaskPlanner? _planner = null;

// Индекс подзадачи, для которой уже добавлен prompt. Раньше prompt добавлялся
// на каждом шаге цикла и обнулял _hasUsedWriteTools: модель писала файл, а
// клиент «забывал» об этом и требовал тот же шаг снова.
int _lastPromptedSubtask = -1;

// Self-check: отслеживаем использование инструментов write/edit для проверки выполнения подзадач.
bool _hasUsedWriteTools = false;

// Пути к файлам, которые модель записала в текущей подзадаче (для верификации существования).
List<string> _writtenFiles = new();

// Локальный HTTP-сервер для preview игры в браузере (ES-модули не работают по file://).
PreviewServer? _previewServer = null;

RefreshHeader();

// Валидация конфига: проверяем, что для текущего провайдера есть API ключ.
if (string.IsNullOrWhiteSpace(api.GetEffectiveKey()))
{
    var keyHint = config.ActiveConnection?.Name ?? "default";
    screen.Write("❌ Нет API ключа для провайдера " + keyHint, ConsoleColor.Red, EntryStyle.Error);
    screen.Write("Установите ключ через: /connect key " + keyHint + " <ваш-ключ>", ConsoleColor.Yellow);
    screen.Write("Или через переменную окружения: IH_API_KEY=<ключ>", ConsoleColor.Yellow);
    screen.Write("");
}

screen.Write("IntellectHub Console — type a message, press F1 for help, or Ctrl+K for commands.", ConsoleColor.DarkGray);
screen.Write("");
await CheckHealthAsync();

// Lines typed while a turn runs wait here. They are handled as ordinary input
// once the turn finishes, so a command or follow-up is never lost.
var queuedInputs = new Queue<string>();

while (true)
{
    var line = queuedInputs.Count > 0
        ? new PromptResult(queuedInputs.Dequeue())
        : screen.ReadLine();
    if (line.Action == PromptAction.Exit) break;
    if (line.Action == PromptAction.CycleApprovals)
    {
        CycleApprovals();
        continue;
    }

    var input = line.Text;
    if (input.Length == 0) continue;

    // Anything with a leading slash is the shell's business.
    var parsed = CommandLine.Parse(input);
    switch (parsed.Kind)
    {
        case InputKind.UnknownCommand:
            ReportUnknownCommand(parsed);
            continue;

        case InputKind.Command:
            if (!await RunCommandAsync(parsed)) return;
            continue;
    }

    screen.Write(input, ConsoleColor.White, EntryStyle.User);
    screen.Write("");
    log.User(input);
    history.Add(new ChatMessage { Role = "user", Content = JsonValue.Create(input) });
    UpdateUsage();

    try
    {
        await RunTurnWithInputAsync();
    }
    catch (OperationCanceledException)
    {
        screen.Write("interrupted", ConsoleColor.DarkGray, EntryStyle.Notice);
        Wire.Write("turn.cancelled", "stopped by the user");
    }
    catch (Exception ex)
    {
        Sound.Error();
        screen.Write("error: " + ex.Message, ConsoleColor.Red, EntryStyle.Error);
        Wire.Write("turn.err", "{0}: {1}", ex.GetType().Name, ex.Message);
        Wire.Block("turn.err.st", ex.ToString());
    }

    UpdateUsage();
    screen.Write("");
}

api.Dispose();
screen.Write("bye", ConsoleColor.DarkGray);

string ResolveProjectRoot()
{
    if (!string.IsNullOrWhiteSpace(config.Project) && Directory.Exists(config.Project))
        return config.Project;

    var cwd = Directory.GetCurrentDirectory();
    config.Project = cwd;
    config.Save();
    return cwd;
}

static string NewSessionKey() => "cli-" + Guid.NewGuid().ToString("N");

/// <summary>
/// Drops the transcript and the remote chat behind it. Both have to go together:
/// a fresh session key against the old history would look to the gateway like an
/// edited conversation, and reusing the key would just make it discard ours.
/// </summary>
void ClearConversation()
{
    history.Clear();
    sessionKey = NewSessionKey();
    screen.Clear();
    UpdateUsage();
    Wire.TraceId = null;
    Wire.Write("session.reset", "transcript cleared locally · new gateway session key {0}", sessionKey);
}

void RefreshHeader()
{
    screen.SetHeader(Banner.Header(config, project));
    screen.SetContext(
        config.Model,
        config.ServerLabel,
        Banner.ProjectLabel(project.Root),
        config.Approvals.ToString().ToLowerInvariant());
    screen.SetTools(tools.Specs.Select(spec => spec.Name));
    UpdateUsage();
}

void UpdateUsage()
{
    var tokens = 0;
    foreach (var message in history)
        tokens += (message.Content?.ToJsonString().Length ?? 0) / 4;
    screen.SetUsage(history.Count, tokens);
}

/// <summary>
/// Remembers a workspace for the project picker. Newest first, capped so the list
/// stays a picker rather than an archive.
/// </summary>
void RememberProject(string root)
{
    var before = config.RecentProjects.ToList();
    config.RecentProjects.RemoveAll(p => string.Equals(p, root, StringComparison.OrdinalIgnoreCase));
    config.RecentProjects.Insert(0, root);
    if (config.RecentProjects.Count > 8)
        config.RecentProjects.RemoveRange(8, config.RecentProjects.Count - 8);

    if (!before.SequenceEqual(config.RecentProjects))
        config.Save();
}

async Task RunTurnWithInputAsync()
{
    using var cancel = new CancellationTokenSource();
    var work = RunTurnAsync(cancel.Token);

    // The prompt stays alive while the turn runs; Enter queues, Esc stops.
    foreach (var next in screen.ComposeWhileBusy(work, cancel))
        queuedInputs.Enqueue(next);

    await work;
}

async Task RunTurnAsync(CancellationToken ct)
{
    try
    {
        await RunTurnCoreAsync(ct);
        // Автоматический переход к следующему режиму, если текущий этап завершён
        // (архитектура+план готовы → код; ошибка в коде → отладка; после отладки → код).
        _modes?.AdvanceIfReady();
    }
    finally
    {
        screen.Idle();
    }
}

async Task RunTurnCoreAsync(CancellationToken ct)
{
    var turnClock = System.Diagnostics.Stopwatch.StartNew();
    var turnId = Wire.NewTraceId();

    // Сколько раз за ход мы уже просили модель перейти от текста к инструментам.
    var modeNudges = 0;

    // Retry counter for when forceToolUse is ignored by the model.
    int _forceRetryCount = 0;
    bool hasForceRetried = false;

    // Новый ход: разрешаем один раз подставить prompt текущей подзадачи.
    // Для задачи без планировщика сбрасываем и tracking записи файлов.
    _lastPromptedSubtask = -1;
    if (_planner is null)
    {
        _hasUsedWriteTools = false;
        _writtenFiles.Clear();
    }

    Wire.TraceId = null;
    Wire.Write("turn", "start · history={0} msgs · tools={1} · planner={2} · mode={3}",
        history.Count, tools.Specs.Count, _planner is not null ? "active" : "none",
        _modes is null ? "-" : _modes.CurrentDisplay);

    // Активный режим проекта выбирается один раз на ход и сохраняется в .IntellectHub.
    // Он добавляет модель контекст: архитектуру, план и лог отладки из .IntellectHub.
    if (_modes is not null)
    {
        var lastUser = history.LastOrDefault(m => m.Role == "user")?.Content?.ToString() ?? "";
        _modes.StartTurn(lastUser);
    }

    for (var step = 0; step < 16; step++)
    {
        ct.ThrowIfCancellationRequested();
        screen.Busy(step == 0 ? "thinking" : "working");

        // One id per request: the same label lands on the client request, on every
        // server line, on the DeepSeek round trip, and on the tool run below.
        var traceId = $"{turnId}.s{step}";
        Wire.TraceId = traceId;
        Wire.Write("step", "{0} of 16 · history={1} msgs", step, history.Count);

        // Если есть активный планировщик и следующая подзадача — просим модель
        // выполнить именно её, а не ждать нового плана.
        if (_planner is not null && _planner.HasNext)
        {
            var subtask = _planner.Current!;
            if (_lastPromptedSubtask != subtask.Index)
            {
                var subtaskPrompt = new ChatMessage
                {
                    Role = "user",
                    Content = JsonValue.Create(
                        $"ПЕРВЫМ ДЕЛОМ просмотри существующие файлы проекта на диске (index.html, script.js, styles.css и т.д.) " +
                        $"— посмотри, что уже готово, что сломано, чего не хватает. " +
                        $"НЕ пиши рабочие файлы в .IntellectHub/ — пиши в корень проекта. " +
                        $"Не дублируй уже существующий код.\n\n" +
                        $"Выполни подзадачу {subtask.Index + 1} из {_planner.Total}: {subtask.Title}\n\n" +
                        $"Описание: {subtask.Description}\n\n" +
                        $"ИСПОЛЬЗУЙ ИНСТРУМЕНТЫ write/edit, чтобы записать свою часть результата в соответствующие файлы проекта. " +
                        $"НЕ ВЫВОДИ ТЕКСТ В ОТВЕТЕ — используй инструменты для записи в файл. " +
                        $"НЕ ПИШИ СКРИПТЫ В ОТВЕТЕ — используй инструмент bash для выполнения команд. " +
                        $"Не пытайся сделать всё за один вызов — пиши только эту часть. " +
                        $"Когда закончишь, напиши краткий отчёт текстом без вызовов инструментов.")
                };
                history.Add(subtaskPrompt);
                // Self-check: сбрасываем флаг использования инструментов только
                // при переходе к новой подзадаче, а не на каждом шаге.
                _hasUsedWriteTools = false;
                _writtenFiles.Clear();
                _lastPromptedSubtask = subtask.Index;
                Wire.Write("planner.subtask", "executing subtask {0}/{1}: {2}", subtask.Index + 1, _planner.Total, subtask.Title);
            }
        }

        AssistantReply reply = null!;
        var callClock = System.Diagnostics.Stopwatch.StartNew();

        // Пока модель не записала ни одного файла в текущем шаге/подзадаче,
        // требуем именно tool call. Это отсекает многоминутные текстовые
        // «архитектуры» и заставляет qwen сразу вызвать write.
        var forceToolUse = !_hasUsedWriteTools &&
            (_planner is not null ||
             (_modes is not null && _modes.Effective is Mode.Architect or Mode.Code or Mode.Debug));

        try
        {
            // Временные ошибки провайдера (HTTP 503, 429, 502, 504) повторяем с
            // экспоненциальной задержкой: 2 с, 4 с, 8 с.
            const int maxRetries = 3;
            Exception? lastTransient = null;
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    reply = await api.SendAsync(_modes?.WithContext(history) ?? history, tools.Specs, traceId, sessionKey, ModelRole.Default, ct, forceToolUse);
                    Sound.Notify();
                    lastTransient = null;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (IsTransientApiError(ex))
                {
                    lastTransient = ex;
                    if (attempt < maxRetries)
                    {
                        var delayMs = (int)Math.Pow(2, attempt) * 1000;
                        Wire.Write("turn", "api transient error (attempt {0}/{1}) · retrying in {2}ms", attempt, maxRetries, delayMs);
                        await Task.Delay(delayMs, ct);
                    }
                }
            }

            if (lastTransient is not null)
                throw lastTransient;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Beep(1047, 60);
            Console.Beep(1318, 80);
            Wire.Write("step.err", "{0}: {1}", ex.GetType().Name, ex.Message);

            // Ошибка сервера/сети (например, "The requested operation requires an element").
            // Записываем в состояние модов и останавливаем ход, не кидая исключение наверх.
            // Модель получит сообщение об ошибке в следующем ходе и сможет повторить запрос.
            _modes?.NoteError("api", ex.Message);

            // Провайдер снял модель с выдачи или её имя сменилось: вместо сырого JSON
            // предлагаем выбрать существующую модель из живого списка, отмечая
            // замену, которую назвал сам провайдер.
            if (ex is ModelUnavailableException unavailable)
            {
                screen.Write($"модель {unavailable.Model} недоступна — провайдер снял её с выдачи или переименовал",
                    ConsoleColor.Yellow, EntryStyle.Error);
                if (unavailable.Suggestion is not null)
                    screen.Write($"провайдер предлагает {unavailable.Suggestion} — она отмечена в списке",
                        ConsoleColor.DarkGray);
                screen.Write("выберите существующую модель:", ConsoleColor.DarkGray);
                await ShowModelsAsync(unavailable.Suggestion);
                screen.Write("после выбора повторите запрос: ↑ и Enter", ConsoleColor.DarkGray);
            }
            else
            {
                screen.Write($"ошибка сервера: {ex.Message}", ConsoleColor.Red, EntryStyle.Error);
            }
            screen.Write("");

            Wire.TraceId = turnId;
            Wire.Write("turn", "api error · {0} · {1} ms", ex.GetType().Name, callClock.ElapsedMilliseconds);
            Wire.Timing("turn.end", turnClock, "finished");
            return;
        }

        Wire.Timing("step", callClock, "round trip finished · stop_reason={0}", reply.StopReason);

        // Если все вызовы инструментов — placeholder-имена (noop, no_tool_available и т.д.),
        // трактуем это как отсутствие реальных вызовов: модель отказалась от инструментов.
        var allPlaceholder = reply.ToolCalls.All(c => IsPlaceholderToolName(c.Name));

        // Don't log/display the model's text response when it has actual tool calls.
        // The model often outputs the tool call as JSON in its text response (explaining what it's doing),
        // which clutters the console. The tool calls themselves are the action.
        if (reply.Text.Trim().Length > 0 && !reply.HasToolCalls)
            log.Thought(reply.Text.Trim());

        // Если модель прислала JSON-план со списком подзадач — загружаем его в
        // планировщик и начинаем выполнение с первой.
        if (_planner is null && allPlaceholder && string.IsNullOrWhiteSpace(reply.Text.Trim()))
        {
            screen.Write("Модель не смогла выполнить задачу за один ход — предлагаю разбить на подзадачи.", ConsoleColor.Yellow);
            var planPrompt = new ChatMessage
            {
                Role = "user",
                Content = JsonValue.Create(
                    "Ты не можешь выполнить эту задачу за один ответ, потому что она слишком большая. " +
                    "Разбей её на подзадачи и верни JSON-план:\n" +
                    "{\"subtasks\":[{\"title\":\"...\",\"description\":\"...\"}, ...]}\n\n" +
                    "Каждая подзадача — это отдельный шаг, который клиент будет выполнять по очереди.")
            };
            history.Add(planPrompt);
            Wire.TraceId = turnId;
            Wire.Write("turn", "noop received · asking model for decomposition plan");
            continue; // next loop iteration will ask model for the plan
        }

        if (_planner is null && !reply.HasToolCalls)
        {
            var maybePlan = TryExtractPlan(reply.Text);
            if (maybePlan is not null)
            {
                _planner = maybePlan;
                _lastPromptedSubtask = -1;
                screen.Write("План принят. Подзадач: " + _planner.Total, ConsoleColor.Yellow);
                screen.Write(_planner.PlanSummary(), ConsoleColor.DarkGray);

                // Сохраняем план в .IntellectHub/plan.md, чтобы он пережил перезапуск
                // и клиент знал, что архитектурный этап пройден.
                var planBuilder = new System.Text.StringBuilder();
                planBuilder.AppendLine("# План реализации");
                planBuilder.AppendLine();
                planBuilder.AppendLine("Автоматически разбит на подзадачи клиентом.");
                planBuilder.AppendLine();
                foreach (var s in _planner.Subtasks)
                    planBuilder.AppendLine($"- [{s.Status}] {s.Index + 1}. {s.Title} — {s.Description}");
                _modeStore!.SavePlan(planBuilder.ToString(), _modes!.State);

                var assistantContent = new JsonArray();
                if (reply.Text.Trim().Length > 0)
                    assistantContent.Add(new JsonObject { ["type"] = "text", ["text"] = reply.Text });
                history.Add(new ChatMessage { Role = "assistant", Content = assistantContent });

                Wire.TraceId = turnId;
                Wire.Write("turn", "plan loaded · {0} subtasks · saved to .IntellectHub/plan.md · continuing with subtask execution", _planner.Total);
                continue; // next loop iteration will pick up the first subtask
            }
        }

        if (!reply.HasToolCalls || allPlaceholder)
        {
            // Auto-режим должен доводить задачу до кода, а не останавливаться на
            // текстовом ответе. Небольшие локальные модели (например,
            // qwen2.5-coder:7b) часто отвечают архитектурой/планом простым текстом
            // вместо вызова write или просто пишут «готово» после записи файлов.
            if (_planner is null && _modes is not null && _modes.Current == Mode.Auto)
            {
                if (TryAdoptArchitectReply(reply.Text))
                {
                    if (reply.Text.Trim().Length > 0)
                        history.Add(new ChatMessage { Role = "assistant", Content = JsonValue.Create(reply.Text) });
                    history.Add(new ChatMessage
                    {
                        Role = "user",
                        Content = JsonValue.Create(
                            "Архитектура и план сохранены в .IntellectHub. Продолжай в режиме Код: " +
                            "вызови инструменты write/edit и создай файлы проекта в корне. " +
                            "Не спрашивай разрешения и не выводи содержимое файлов текстом.")
                    });
                    Wire.TraceId = turnId;
                    Wire.Write("turn", "architect artifacts ready · continuing in code mode");
                    continue;
                }

                // В режиме Код модель тоже иногда отвечает текстом, не создав файлов.
                // Даём до двух жёстких напоминаний, затем завершаем ход как обычно.
                if (_modes.Effective == Mode.Code && !_modes.State.CodeReady && modeNudges < 2)
                {
                    modeNudges++;
                    if (reply.Text.Trim().Length > 0)
                        history.Add(new ChatMessage { Role = "assistant", Content = JsonValue.Create(reply.Text) });
                    history.Add(new ChatMessage
                    {
                        Role = "user",
                        Content = JsonValue.Create(
                            "Ты в режиме Код. Не объясняй и не спрашивай разрешения — вызови инструменты " +
                            "write/edit и запиши файлы проекта. Не выводи код текстом. " +
                            "Если работа уже выполнена, напиши краткий отчёт.")
                    });
                    Wire.TraceId = turnId;
                    Wire.Write("turn", "text-only reply in auto/code · nudging model to use tools ({0}/2)", modeNudges);
                    continue;
                }
            }

            // Если модель вернула пустой noop-вызов без текста — она не справилась с
            // задачей за один ход. Предлагаем ей разбить задачу на подзадачи.
            if (_planner is null && string.IsNullOrWhiteSpace(reply.Text.Trim()))
            {
                screen.Write("Модель не смогла выполнить задачу за один ход — предлагаю разбить на подзадачи.", ConsoleColor.Yellow);
                var planPrompt = new ChatMessage
                {
                    Role = "user",
                    Content = JsonValue.Create(
                        "Ты не можешь выполнить эту задачу за один ответ, потому что она слишком большая. " +
                        "Разбей её на подзадачи и верни JSON-план:\n" +
                        "{\"subtasks\":[{\"title\":\"...\",\"description\":\"...\"}, ...]}\n\n" +
                        "Каждая подзадача — это отдельный шаг, который клиент будет выполнять по очереди.")
                };
                history.Add(planPrompt);
                Wire.TraceId = turnId;
                Wire.Write("turn", "noop received · asking model for decomposition plan");
                continue;
            }

            if (_planner is not null)
            {
                var currentSubtask = _planner.Current;
                if (currentSubtask is not null)
                {
                    const int maxAttempts = 3;
                    var isLastSubtask = !_planner.HasNext;

                    // 1. Пустой ответ/placeholder: если файлы уже записаны, это просто
                    //    отсутствие отчёта — завершаем подзадачу. Иначе просим повторить.
                    if (string.IsNullOrWhiteSpace(reply.Text.Trim()) || allPlaceholder)
                    {
                        var wroteFiles = config.SelfCheck && HasUsedWriteTools() && VerifyWrittenFiles();
                        if (wroteFiles)
                        {
                            if (!await TryCompleteCurrentSubtask("", turnId, history, verifyBrowser: isLastSubtask))
                                continue; // проверка не прошла — добавлен retry-промпт
                        }
                        else
                        {
                            currentSubtask.Attempts++;
                            if (currentSubtask.Attempts >= maxAttempts)
                            {
                                screen.Write($"Подзадача {currentSubtask.Index + 1} не выполнена после {maxAttempts} попыток — пропускаем.", ConsoleColor.Red);
                                _planner.MarkCurrentFailed("Model returned empty or placeholder responses too many times.");
                                Wire.TraceId = turnId;
                                Wire.Write("turn", "subtask {0} failed after {1} attempts · skipping", currentSubtask.Index + 1, maxAttempts);

                                var failContent = new JsonArray();
                                failContent.Add(new JsonObject { ["type"] = "text", ["text"] = $"[Подзадача {currentSubtask.Index + 1} не выполнена — пропускаем]" });
                                history.Add(new ChatMessage { Role = "assistant", Content = failContent });
                            }
                            else
                            {
                                screen.Write($"Подзадача {currentSubtask.Index + 1} не выполнена — модель не прислала содержимое, повторяем запрос (попытка {currentSubtask.Attempts}/{maxAttempts}).", ConsoleColor.Yellow);
                                var retryPrompt = new ChatMessage
                                {
                                    Role = "user",
                                    Content = JsonValue.Create(
                                        $"Ты должен написать содержимое подзадачи {currentSubtask.Index + 1} из {_planner.Total}: \"{currentSubtask.Title}\".\n\n" +
                                        $"Описание: {currentSubtask.Description}\n\n" +
                                        $"Напиши текст раздела целиком, используя инструмент write/edit. Не присылай пустой ответ и не отказывайся от инструментов.")
                                };
                                history.Add(retryPrompt);
                                Wire.TraceId = turnId;
                                Wire.Write("turn", "subtask {0} empty · retrying with stricter prompt (attempt {1}/{2})", currentSubtask.Index + 1, currentSubtask.Attempts, maxAttempts);
                                continue; // ask model again for this subtask
                            }
                        }
                    }
                    // 2. Модель ответила текстом, но ни разу не использовала write/edit.
                    else if (config.SelfCheck && !HasUsedWriteTools())
                    {
                        currentSubtask.Attempts++;
                        if (currentSubtask.Attempts >= maxAttempts)
                        {
                            screen.Write($"Подзадача {currentSubtask.Index + 1} не выполнена после {maxAttempts} попыток (модель не использовала инструменты) — пропускаем.", ConsoleColor.Red);
                            _planner.MarkCurrentFailed("Model did not use write/edit tools after multiple attempts.");
                            Wire.TraceId = turnId;
                            Wire.Write("turn", "subtask {0} failed after {1} attempts (no write tools) · skipping", currentSubtask.Index + 1, maxAttempts);
                        }
                        else
                        {
                            screen.Write($"Подзадача {currentSubtask.Index + 1}: модель не использовала инструменты — повторяем запрос (попытка {currentSubtask.Attempts}/{maxAttempts}).", ConsoleColor.Yellow);
                            var retryPrompt = new ChatMessage
                            {
                                Role = "user",
                                Content = JsonValue.Create(
                                    $"Ты должен написать содержимое подзадачи {currentSubtask.Index + 1} из {_planner.Total}: \"{currentSubtask.Title}\".\n\n" +
                                    $"Описание: {currentSubtask.Description}\n\n" +
                                    $"ИСПОЛЬЗУЙ ИНСТРУМЕНТЫ write/edit, чтобы записать содержимое в соответствующие файлы проекта. " +
                                    $"НЕ ВЫВОДИ ТЕКСТ В ОТВЕТЕ — используй инструменты для записи в файл. " +
                                    $"НЕ ПИШИ СКРИПТЫ В ОТВЕТЕ — используй инструмент bash для выполнения команд.")
                            };
                            history.Add(retryPrompt);
                            Wire.TraceId = turnId;
                            Wire.Write("turn", "subtask {0} no write tools · retrying (attempt {1}/{2})", currentSubtask.Index + 1, currentSubtask.Attempts, maxAttempts);
                            continue;
                        }
                    }
                    // 3. Модель написала файлы и прислала отчёт: проверяем файлы и
                    //    браузер и завершаем подзадачу ровно один раз.
                    else if (config.SelfCheck)
                    {
                        if (!await TryCompleteCurrentSubtask(reply.Text.Trim(), turnId, history, verifyBrowser: isLastSubtask))
                            continue; // проверка не прошла — добавлен retry-промпт

                        var assistantContent = new JsonArray();
                        if (reply.Text.Trim().Length > 0)
                            assistantContent.Add(new JsonObject { ["type"] = "text", ["text"] = reply.Text });
                        history.Add(new ChatMessage { Role = "assistant", Content = assistantContent });
                    }
                    // 4. Self-check выключен: принимаем отчёт без проверки.
                    else
                    {
                        _planner.MarkCurrentDone(reply.Text.Trim());
                        screen.Write($"Подзадача {currentSubtask.Index + 1} готова.", ConsoleColor.Green);
                        history.Add(new ChatMessage { Role = "assistant", Content = JsonValue.Create(reply.Text) });
                    }

                    // 5. Следующая подзадача или завершение плана.
                    if (_planner is not null && _planner.HasNext)
                    {
                        Wire.TraceId = turnId;
                        Wire.Write("turn", "subtask done · continuing with subtask {0}/{1}",
                            _planner.Current!.Index + 1, _planner.Total);
                        continue; // execute next subtask
                    }

                    if (_planner is not null && !_planner.HasNext)
                    {
                        screen.Write("Все подзадачи выполнены.", ConsoleColor.Green);
                        _planner = null;

                        // Переходим к следующему режиму по порядку (Architect → Code → Debug → Code …).
                        _modes?.AdvanceToNextMode();

                        // Back to the turn id: the step loop overwrote it.
                        Wire.TraceId = turnId;
                        Wire.Write("turn", "done · all subtasks complete · {0} steps · {1} msgs in history", step + 1, history.Count);
                        Wire.Timing("turn.end", turnClock, "finished");
                        return;
                    }
                }
            }

            // If we forced tool use but got text without tool calls, retry with stricter prompt.
            // This handles models (especially local ones) that ignore tool_choice: required.
            if (forceToolUse && !reply.HasToolCalls)
            {
                const int maxForceRetries = 3;
                if (!hasForceRetried)
                {
                    _forceRetryCount = 0;
                    hasForceRetried = true;
                }

                if (_forceRetryCount < maxForceRetries)
                {
                    _forceRetryCount++;
                    screen.Write($"Модель не использовала инструменты (forceToolUse) — повторяем запрос (попытка {_forceRetryCount}/{maxForceRetries}).", ConsoleColor.Yellow);
                    var retryPrompt = new ChatMessage
                    {
                        Role = "user",
                        Content = JsonValue.Create(
                            "Ты должен использовать инструменты write/edit для записи файлов. " +
                            "НЕ ВЫВОДИ ТЕКСТ В ОТВЕТЕ — используй инструменты для записи в файл. " +
                            "НЕ ПИШИ СКРИПТЫ В ОТВЕТЕ — используй инструмент bash для выполнения команд. " +
                            "Вызови write для каждого файла, который нужно создать.")
                    };
                    history.Add(retryPrompt);
                    Wire.TraceId = turnId;
                    Wire.Write("turn", "forceToolUse ignored · retrying with stricter prompt (attempt {0}/{1})", _forceRetryCount, maxForceRetries);
                    continue; // next loop iteration will ask model again
                }
                else
                {
                    screen.Write("Модель не использовала инструменты после 3 попыток с forceToolUse — прерываем.", ConsoleColor.Red);
                    Wire.Write("turn", "forceToolUse failed after 3 retries · aborting");
                }
            }

            if (reply.Text.Trim().Length > 0)
                await RevealAsync(reply.Text.Trim());
            history.Add(new ChatMessage { Role = "assistant", Content = JsonValue.Create(reply.Text) });

            // Back to the turn id: the step loop overwrote it.
            Wire.TraceId = turnId;
            Wire.Write("turn", "done · no tool calls · {0} steps · {1} msgs in history", step + 1, history.Count);
            Wire.Timing("turn.end", turnClock, "finished");
            return;
        }

        var assistantContent2 = new JsonArray();
        if (reply.Text.Trim().Length > 0)
            assistantContent2.Add(new JsonObject { ["type"] = "text", ["text"] = reply.Text });

        foreach (var call in reply.ToolCalls)
        {
            assistantContent2.Add(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = call.Id,
                ["name"] = call.Name,
                ["input"] = JsonNode.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments),
            });
        }
        history.Add(new ChatMessage { Role = "assistant", Content = assistantContent2 });

        // Подзадача завершается только после выполнения инструментов и
        // текстового отчёта модели (см. ветку без tool calls выше). Раньше здесь
        // стояла проверка HasUsedWriteTools() — но она срабатывала ДО выполнения
        // текущих вызовов, поэтому следующий write/edit мог быть пропущен.

        var results = new JsonArray();
        var stop = false;
        var showedAutoThought = false;

        foreach (var original in reply.ToolCalls)
        {
            ct.ThrowIfCancellationRequested();
            var call = original;

            while (true)
            {
                // The approval mode decides whether this call stops for a person at
                // all. Skipping the barrier entirely — rather than answering it on
                // the user's behalf — is what makes unattended runs actually run.
                var autoApproved = ToolRisk.Allowed(config.Approvals, call.Name);
                
                // Show preview for approval if not auto-approved
                if (!autoApproved)
                {
                    if (call.Name == "write")
                    {
                        var content = ReadArg(call.Arguments, "content");
                        screen.Write($"WRITE PREVIEW ({Trim(ReadArg(call.Arguments, "path"), 100)}):\n{Fold(content, 5, 5)}", ConsoleColor.Yellow);
                    }
                    else if (call.Name == "edit")
                    {
                        var oldString = ReadArg(call.Arguments, "oldString");
                        var newString = ReadArg(call.Arguments, "newString");
                        screen.Write($"EDIT PREVIEW ({Trim(ReadArg(call.Arguments, "path"), 100)}):\n--- OLD ---\n{oldString}\n--- NEW ---\n{newString}", ConsoleColor.Yellow);
                    }
                }

                var choice = autoApproved ? BarrierChoice.Run : screen.Ask(reply.Text.Trim(), Describe(call));

                Wire.Write("barrier", "{0} → {1}", call.Name,
                    autoApproved ? $"auto ({config.ApprovalMode})" : choice.ToString());

                // The barrier is what put the model's reasoning on screen. With it
                // skipped the turn would otherwise run silently, so it is shown once
                // per batch of calls instead.
                if (autoApproved && !showedAutoThought)
                {
                    if (reply.Text.Trim().Length > 0)
                    {
                        screen.Write(reply.Text.Trim(), ConsoleColor.DarkCyan, EntryStyle.Thought);
                        screen.Write("");
                    }
                    showedAutoThought = true;
                }

                if (choice == BarrierChoice.Quit)
                {
                    results.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = call.Id,
                        ["content"] = "The user stopped the session before this action ran.",
                        ["is_error"] = true,
                    });
                    stop = true;
                    break;
                }

                if (choice == BarrierChoice.Edit)
                {
                    screen.HoldKeyboard();
                    try
                    {
                        call = EditCall(call);
                    }
                    finally
                    {
                        screen.ReleaseKeyboard();
                    }
                    screen.Write(Describe(call), ConsoleColor.White, EntryStyle.Tool);
                    Wire.Write("barrier.edit", "{0} → new arguments", call.Name);
                    Wire.Json("barrier.edit.args", call.Arguments);
                    continue;
                }

                // The barrier already showed the call when a person approved it.
                // Unattended calls would otherwise run silently, so they print here.
                if (autoApproved)
                    screen.Write(Describe(call), ConsoleColor.White, EntryStyle.Tool);

                log.Action(call.Name, call.Arguments);
                Wire.Write("tool", "→ {0} · project={1}", call.Name, project.Root);
                Wire.Json("tool.args", call.Arguments);

                // Self-check: отслеживаем использование инструментов write/edit в текущей подзадаче.
                if (call.Name is "write" or "edit")
                {
                    _hasUsedWriteTools = true;
                    var path = ReadArg(call.Arguments, "path");
                    if (path.Length > 0 && !_writtenFiles.Contains(path))
                        _writtenFiles.Add(path);

                    // Записываем артефакт мода в .IntellectHub и обновляем флаги готовности.
                    var content = ReadArg(call.Arguments, "content");
                    if (call.Name == "write")
                    {
                        _modes?.RecordArtifact(_modes.Effective, path, content);
                    }
                    else if (call.Name == "edit")
                    {
                        // Для edit читаем текущее содержимое файла, чтобы передать в RecordArtifact.
                        var existing = _modeStore is not null && _modeStore.TryReadArtifact(path, out var current)
                            ? current
                            : "";
                        _modes?.RecordArtifact(_modes.Effective, path, existing);
                    }
                }

                var toolClock = System.Diagnostics.Stopwatch.StartNew();
                var result = await tools.ExecuteAsync(call);
                if (result.IsError) _modes?.NoteError(call.Name, result.Output);
                ct.ThrowIfCancellationRequested();
                if (result.IsError) Sound.Error(); else Sound.Success();
                Wire.Timing("tool.done", toolClock, "{0} · is_error={1} · {2} chars of output",
                    call.Name, result.IsError, result.Output.Length);
                Wire.Block("tool.output", result.Output);

                log.Result(result.Output);

                screen.Write((result.IsError ? "error · " : "ok · ") + Fold(result.Output),
                    result.IsError ? ConsoleColor.Red : ConsoleColor.DarkGray, EntryStyle.ToolOutput);

                // Если инструмент вернул ошибку — говорим модели чётко: исправь аргументы и повтори.
                // Иначе модель может просто сдаться (как было с glob { pattern }).
                var toolContent = result.IsError
                    ? $"Ошибка выполнения инструмента '{call.Name}': {result.Output}\n\nИсправь аргументы и вызови этот же инструмент повторно с правильными параметрами. Не пропускай этот шаг."
                    : result.Output;

                results.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = call.Id,
                    ["content"] = toolContent,
                    ["is_error"] = result.IsError,
                });

                // Don't pause for client-side validation errors (missing args, invalid JSON).
                // These are fixable by the model immediately - let it retry in the next step.
                // Execution errors (file not found, permissions, etc.) should still pause.
                var isValidationError = result.IsError &&
                    (result.Output.StartsWith("Missing required arguments:", StringComparison.Ordinal) ||
                     result.Output.StartsWith("Invalid arguments:", StringComparison.Ordinal) ||
                     result.Output.StartsWith("oldString not found", StringComparison.Ordinal) ||
                     result.Output.StartsWith("oldString is not unique", StringComparison.Ordinal) ||
                     result.Output.StartsWith("path is empty", StringComparison.Ordinal) ||
                     result.Output.StartsWith("path names no file", StringComparison.Ordinal) ||
                     result.Output.StartsWith("Path escapes workspace", StringComparison.Ordinal));

                // При ошибке инструмента — ОСТАНАВЛИВАЕМ ход, не продолжаем нестись дальше.
                // Модель получит ошибку в следующем ходе и должна её исправить.
                if (result.IsError && !isValidationError)
                {
                    stop = true;
                    break;
                }

                if (choice == BarrierChoice.Step) stop = true;
                break;
            }

            if (stop) break;
        }

        history.Add(new ChatMessage { Role = "user", Content = results });

        // Подзадача завершается в текстовой ветке, после отчёта модели.
        // Здесь раньше стояло завершение сразу после write: браузерная проверка
        // запускалась до того, как модель закончила шаг, и подзадача уходила в
        // бесконечный retry. Текстовый путь проверяет файлы и браузер один раз.

        if (stop)
        {
            screen.Write("(paused)", ConsoleColor.DarkGray);
            Wire.TraceId = turnId;
            Wire.Write("turn", "paused at the barrier · history={0} msgs", history.Count);
            Wire.Timing("turn.end", turnClock, "finished");
            return;
        }
    }

    screen.Write("(stopped after 16 tool steps)", ConsoleColor.DarkGray);
    Wire.TraceId = turnId;
    Wire.Write("turn", "hit the 16-step cap without a final answer");
    Wire.Timing("turn.end", turnClock, "finished");
}

    /// <summary>Определяет, является ли ошибка API временной (что имеет смысл повторять).</summary>
    static bool IsTransientApiError(Exception ex)
    {
        if (ex is HttpRequestException hre)
        {
            var msg = ex.Message;
            return msg.Contains("503") || msg.Contains("429") || msg.Contains("502") || msg.Contains("504");
        }
        return false;
    }

static string Describe(ToolCall call)
{
    try
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                var value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.GetRawText();
                if (!string.IsNullOrWhiteSpace(value))
                    return $"{call.Name}: {Trim(value, 200)}";
            }
        }
    }
    catch (JsonException) { }

    return call.Name;
}

ToolCall EditCall(ToolCall call)
{
    var argName = PrimaryArg(call.Name);
    var current = ReadArg(call.Arguments, argName);

    screen.Write($"  {argName} [{Trim(current, 200)}]", ConsoleColor.DarkGray);
    var edited = screen.ReadLine($"{argName} > ");
    if (edited.Action == PromptAction.Exit || edited.Text.Length == 0) return call;

    var node = JsonNode.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments) as JsonObject ?? new JsonObject();
    node[argName] = edited.Text;
    return new ToolCall { Id = call.Id, Name = call.Name, Arguments = node.ToJsonString() };
}

static string PrimaryArg(string tool) => tool switch
{
    "bash" => "command",
    "read" => "path",
    "write" => "path",
    "glob" => "pattern",
    "edit" => "newString",
    "grep" => "pattern",
    "check" => "path",
    _ => "input",
};



static string ReadArg(string arguments, string name)
{
    try
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var value))
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
    }
    catch (JsonException) { }
    return "";
}

static string Fold(string text, int head = 5, int tail = 5)
{
    var lines = text.Replace("\r\n", "\n").Split('\n');
    if (lines.Length <= head + tail + 1)
        return text;

    var hidden = lines.Length - head - tail;
    var shown = lines.Take(head)
        .Append($"  ... {hidden} lines hidden ...")
        .Concat(lines.Skip(lines.Length - tail));
    return string.Join('\n', shown);
}

static string Trim(string value, int max)
{
    var single = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    return single.Length > max ? single[..max] + "..." : single;
}

/// <summary>
/// Пытается извлечь JSON-план со списком подзадач из текста ответа модели.
/// Ожидается формат {"subtasks":[{"title":"...","description":"..."}, ...]}.
/// JSON может быть обёрнут в markdown-блок ```json ... ```.
/// </summary>
static TaskPlanner? TryExtractPlan(string text)
{
    if (string.IsNullOrWhiteSpace(text)) return null;

    try
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        var json = text[start..(end + 1)];
        var planner = new TaskPlanner();
        planner.LoadFromPlan(json);
        if (planner.Total == 0) return null;
        return planner;
    }
    catch
    {
        return null;
    }
}

async Task RevealAsync(string text)
{
    const int chunk = 3;
    if (text.Length <= chunk)
    {
        screen.Write(text, ConsoleColor.White, EntryStyle.Assistant);
        return;
    }

    screen.BeginStream(ConsoleColor.White, EntryStyle.Assistant);
    for (var i = 0; i < text.Length; i += chunk)
    {
        screen.PushStream(text.Substring(i, Math.Min(chunk, text.Length - i)));
        await Task.Delay(8);
    }
    screen.EndStream();
}

async Task CheckHealthAsync()
{
    var baseUrl = config.BaseUrl ?? "";
    var isOpenAICompatible = baseUrl.Contains("openrouter", StringComparison.OrdinalIgnoreCase)
                          || baseUrl.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
                          || baseUrl.Contains("localhost:11434", StringComparison.OrdinalIgnoreCase)
                          || baseUrl.Contains("127.0.0.1:11434", StringComparison.OrdinalIgnoreCase)
                          || baseUrl.Contains(":11434", StringComparison.OrdinalIgnoreCase);

    var isLocal = config.BaseUrl.StartsWith("http://127.0.0.1") ||
                  config.BaseUrl.StartsWith("http://localhost");

    if (!isLocal || isOpenAICompatible)
    {
        // External providers (Google, Anthropic, OpenAI, Ollama, etc.) don't have a /health
        // endpoint. Just confirm the address is configured.
        screen.Write("connected to " + config.BaseUrl, ConsoleColor.DarkGray);
        Wire.TraceId = null;
        Wire.Write("health", "external provider at {0}", config.BaseUrl);
        screen.Write("");
        return;
    }

    try
    {
        await api.HealthAsync();
        screen.Write("connected to " + config.BaseUrl, ConsoleColor.DarkGray);
        Wire.TraceId = null;
        Wire.Write("health", "gateway reachable at {0}", config.BaseUrl);
    }
    catch (HttpRequestException ex)
    {
        screen.Write("cannot reach " + config.BaseUrl, ConsoleColor.Red);
        screen.Write("  the server is not listening there", ConsoleColor.DarkGray);
        screen.Write("  start IntellectHub, or point elsewhere: /connect <url>", ConsoleColor.DarkGray);
        Wire.TraceId = null;
        Wire.Write("health.err", "gateway unreachable at {0}: {1}", config.BaseUrl, ex.Message);
    }
    catch (Exception ex)
    {
        screen.Write("cannot reach " + config.BaseUrl + ": " + ex.Message, ConsoleColor.Red);
        Wire.TraceId = null;
        Wire.Write("health.err", "gateway unhealthy at {0}: {1}", config.BaseUrl, ex.Message);
    }
    screen.Write("");
}

async Task ShowModelsAsync(string? recommended = null)
{
    IReadOnlyList<string> models;
    try
    {
        models = await api.ListModelsAsync();
    }
    catch (Exception ex)
    {
        screen.Write("error: " + ex.Message, ConsoleColor.Red, EntryStyle.Error);
        return;
    }

    if (models.Count == 0)
    {
        screen.Write("(no models)", ConsoleColor.DarkGray);
        return;
    }

    if (!screen.IsInteractive)
    {
        screen.Write(string.Join("\n", models));
        return;
    }

    var current = models.ToList().FindIndex(m => string.Equals(m, config.Model, StringComparison.OrdinalIgnoreCase));
    var items = models
        .Select(m =>
        {
            string? badge = null;
            if (recommended is not null && string.Equals(ModelName.Short(m), recommended, StringComparison.OrdinalIgnoreCase))
                badge = "recommended";
            else if (string.Equals(m, config.Model, StringComparison.OrdinalIgnoreCase))
                badge = "current";
            return new PickItem(m, null, badge);
        })
        .ToList();

    var initial = recommended is null
        ? Math.Max(0, current)
        : Math.Max(0, items.FindIndex(i => i.Badge == "recommended"));
    var picked = screen.Pick("models", items, "↑↓ move · ⏎ use model · Esc close", initial);
    if (picked is null) return;

    var model = ModelName.Short(models[picked.Value]);
    if (string.Equals(model, config.Model, StringComparison.OrdinalIgnoreCase))
    {
        screen.Write($"model is already {model}", ConsoleColor.DarkGray);
        return;
    }

    // Для именованного подключения эффективная модель берётся из записи
    // подключения, а не из верхнего config.Model, поэтому обновляем обе: иначе
    // выбор молча не применяется к последующим запросам.
    config.Model = model;
    var savedConnection = config.Connections.FirstOrDefault(c =>
        string.Equals(c.Name, config.ConnectionName, StringComparison.OrdinalIgnoreCase));
    if (savedConnection is not null) savedConnection.Model = model;
    config.Save();
    Reconnect();
    Wire.TraceId = null;
    Wire.Write("model", "{0}", model);
    screen.Write($"model = {model}", ConsoleColor.Green, EntryStyle.Notice);
}

/// <summary>
/// Runs an internal command. Returns false to leave the REPL.
/// </summary>
async Task<bool> RunCommandAsync(ParsedInput parsed)
{
    switch (parsed.Command!.Name)
    {
        case "/exit":
            return false;

        case "/help":
            ShowHelp();
            return true;

        case "/clear":
            ClearConversation();
            return true;

        case "/plan":
            await PlanCurrentTaskAsync();
            return true;

        case "/models":
            await ShowModelsAsync();
            return true;

        case "/config":
            await EditConfigAsync(parsed.Arguments);
            return true;

        case "/connect":
            await ConnectAsync(parsed.Arguments);
            return true;

        case "/approve":
            SetApprovals(parsed.Arguments);
            return true;

        case "/project":
            await SelectProjectAsync(parsed.Arguments);
            return true;

        case "/mode":
            ModeCommandAsync(parsed.Arguments);
            return true;

        case "/preview":
            await PreviewCommandAsync(parsed.Arguments);
            return true;

        default:
            screen.Write($"not wired up: {parsed.Command.Name}", ConsoleColor.Red);
            return true;
    }
}

void ReportUnknownCommand(ParsedInput parsed)
{
    Wire.TraceId = null;
    Wire.Write("command.err", "unknown command {0} · suggestion={1}", parsed.Token, parsed.Suggestion ?? "(none)");

    screen.Write($"unknown command {parsed.Token}", ConsoleColor.Red);
    if (parsed.Suggestion is { } suggestion)
        screen.Write($"did you mean {suggestion}?", ConsoleColor.Yellow);

    screen.Write("type /help for the list", ConsoleColor.DarkGray);
}

/// <summary>
    /// Switches provider. Takes a named connection (a built-in preset or one the user
    /// saved), a saved gateway by name, or a bare URL to use an address that was never
    /// saved — all three land in the same place, and a named switch also updates the
    /// stored entry so the address stays in step with the name.
    /// </summary>
    async Task ConnectAsync(string arguments)
    {
        var (verb, rest) = Split(arguments);

        if (verb.Length == 0)
        {
            await PickConnectionAsync();
            return;
        }

        if (verb.Equals("save", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("add", StringComparison.OrdinalIgnoreCase))
        {
            SaveServer(rest);
            return;
        }

        if (verb.Equals("forget", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            ForgetServer(rest);
            return;
        }

        // The credential for a connection lives in secrets.json, not in the
        // readable configuration, so it gets its own verb.
        if (verb.Equals("key", StringComparison.OrdinalIgnoreCase))
        {
            SetConnectionKey(rest);
            return;
        }

        var picked = config.AllConnections.FirstOrDefault(c =>
            string.Equals(c.Name, verb, StringComparison.OrdinalIgnoreCase));

        if (picked is not null)
        {
            ApplyConnection(picked);
            Reconnect();
            await CheckHealthAsync();
            return;
        }

        var known = config.Servers.FirstOrDefault(s =>
            string.Equals(s.Name, verb, StringComparison.OrdinalIgnoreCase));

        if (known is not null)
        {
            config.BaseUrl = known.Url;
            config.ServerName = known.Name;
            config.ConnectionName = "";
            config.Save();
            AfterServerChange($"{known.Name} → {known.Url}");
        }
        else
        {
            // Not a saved name, so it has to be an address.
            var setting = Setting.Find("server");
            if (setting is null) return;

            var error = setting.Validate(verb);
            if (error is not null)
            {
                screen.Write(error, ConsoleColor.Red);
                screen.Write("known connections: " + KnownConnectionList(), ConsoleColor.DarkGray);
                screen.Write("known servers: " + KnownServerList(), ConsoleColor.DarkGray);
                screen.Write("save one with /connect save <name> <url>", ConsoleColor.DarkGray);
                return;
            }

            setting.Write(config, verb);
            config.ServerName = "";
            config.ConnectionName = "";
            config.Save();
            AfterServerChange($"custom → {config.BaseUrl}");
        }

        await CheckHealthAsync();
    }

    /// <summary>
    /// Points the client at a named connection and records it. The address, the model
    /// and the credential all follow the connection, so a provider switch does not
    /// leave the previous provider's model behind.
    /// </summary>
    void ApplyConnection(ConnectionEntry target)
    {
        config.ApplyConnection(target);
        config.Save();

        Wire.TraceId = null;
        Wire.Write("connection", "{0} → {1} · model={2}", target.Name, config.BaseUrl, config.Model);

        Reconnect();
        screen.Write($"provider = {target.Name} → {config.BaseUrl}", ConsoleColor.Green);
        screen.Write($"model = {config.Model}", ConsoleColor.DarkGray);

        // A provider with no credential cannot answer. Say so here, where the key can
        // still be entered, rather than on the next message as a 401.
        if (target.Provider != ProviderKind.CloudflareR2 &&
            string.IsNullOrWhiteSpace(ClientConfig.GetSecret(target.CredentialKey)) &&
            string.IsNullOrWhiteSpace(config.ApiKey))
        {
            screen.Write($"no key for {target.Name} — set one with /connect key {target.Name}", ConsoleColor.Yellow);
        }
    }

    /// <summary>
    /// Stores or clears the credential for a connection. The value goes to
    /// secrets.json so client.json stays safe to share.
    /// </summary>
    void SetConnectionKey(string rest)
    {
        var (name, value) = Split(rest);
        if (name.Length == 0)
        {
            screen.Write("usage: /connect key <name> [value]", ConsoleColor.Red);
            return;
        }

        var target = config.AllConnections.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            screen.Write($"no connection named '{name}'", ConsoleColor.Red);
            screen.Write("known: " + KnownConnectionList(), ConsoleColor.DarkGray);
            return;
        }

        if (value.Length == 0)
        {
            var current = ClientConfig.GetSecret(target.CredentialKey);
            screen.Write($"  key for {target.Name}", ConsoleColor.DarkGray);
            screen.Write($"  now: {(current is null ? "(none)" : Wire.Mask(current))}", ConsoleColor.DarkGray);
            screen.Write("  type ! to remove it", ConsoleColor.DarkGray);

            value = screen.ReadLine($"key > ").Text;
            if (value.Length == 0) return;
        }
        if (value == "!")
        {
            var removed = ClientConfig.DeleteSecret(target.CredentialKey);
            screen.Write(removed
                ? $"key for {target.Name} removed"
                : $"no key stored for {target.Name}", removed ? ConsoleColor.Green : ConsoleColor.Yellow);
            Reconnect();
            return;
        }

        ClientConfig.SaveSecret(target.CredentialKey, value);
        screen.Write($"key for {target.Name} saved to {ClientConfig.SecretsPath}", ConsoleColor.Green);
        Reconnect();
    }

    void AfterServerChange(string shown)
    {
        Wire.TraceId = null;
        Wire.Write("server", "{0}", shown);
        Reconnect();
        screen.Write($"server = {shown}", ConsoleColor.Green);
    }

    /// <summary>
    /// The no-argument form of /connect. Lists every connection the client can switch
    /// to: the built-in presets first, then saved gateways, and a slot for an address
    /// typed by hand or one remembered under a name.
    /// </summary>
    async Task PickConnectionAsync()
    {
        if (!screen.IsInteractive)
        {
            ShowServers();
            return;
        }

        var items = new List<PickItem>();
        // Each row carries its action so a header line is not selectable.
        var actions = new List<string>(); // "connection:<name>", "header", "server:<name>", "custom", "save"

        foreach (var row in config.AllConnections)
        {
            var active = string.Equals(row.Name, config.ConnectionName, StringComparison.OrdinalIgnoreCase);
            items.Add(new PickItem(row.Name, row.Url,
                active ? "active" : (HasCredential(row) ? null : "no key"), active));
            actions.Add($"connection:{row.Name}");
        }

        if (config.Servers.Count > 0)
        {
            items.Add(new PickItem("— saved gateways —", "", null));
            actions.Add("header");
            foreach (var server in config.Servers)
            {
                var active = string.Equals(server.Name, config.ServerName, StringComparison.OrdinalIgnoreCase);
                items.Add(new PickItem(server.Name, server.Url, active ? "active" : null, active));
                actions.Add($"server:{server.Name}");
            }
        }

        var customAt = items.Count;
        items.Add(new PickItem("custom address…", "type a URL and switch to it"));
        actions.Add("custom");

        var saveAt = items.Count;
        items.Add(new PickItem("save current…", "remember the current address under a name"));
        actions.Add("save");

        var picked = screen.Pick("connections", items, "↑↓ move · ⏎ select · Esc close");
        if (picked is null) return;

        if (picked.Value == saveAt)
        {
            var name = screen.ReadLine("save as > ");
            if (name.Action == PromptAction.Exit || name.Text.Length == 0) return;
            SaveServer(name.Text + " " + config.BaseUrl);
            return;
        }

        if (picked.Value == customAt)
        {
            var answer = screen.ReadLine("server > ", config.BaseUrl);
            if (answer.Action == PromptAction.Exit || answer.Text.Length == 0) return;

            var setting = Setting.Find("server");
            var error = setting?.Validate(answer.Text);
            if (error is not null)
            {
                screen.Write(error, ConsoleColor.Red);
                return;
            }

            setting!.Write(config, answer.Text);
            config.ServerName = "";
            config.ConnectionName = "";
            config.Save();
            AfterServerChange($"custom → {config.BaseUrl}");
            await CheckHealthAsync();
            return;
        }

        var action = actions[picked.Value];
        if (action == "header" || action == "custom") return;

        var colon = action.IndexOf(':');
        var kind = colon >= 0 ? action[..colon] : action;
        var target = colon >= 0 ? action[(colon + 1)..] : action;

        if (kind == "server")
        {
            var gateway = config.Servers.First(s => string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase));
            config.BaseUrl = gateway.Url;
            config.ServerName = gateway.Name;
            config.ConnectionName = "";
            config.Save();
            AfterServerChange($"{gateway.Name} → {gateway.Url}");
            await CheckHealthAsync();
            return;
        }

        var selected = config.AllConnections.First(c => string.Equals(c.Name, target, StringComparison.OrdinalIgnoreCase));
        ApplyConnection(selected);
        await CheckHealthAsync();
    }

    /// <summary>
    /// Whether a connection has the credential it needs. Storage is asked about
    /// separately: it is configured through the environment or the r2:* entries
    /// in secrets.json, not through a key stored under the connection's name.
    /// </summary>
    bool HasCredential(ConnectionEntry entry)
    {
        if (entry.Provider == ProviderKind.CloudflareR2)
        {
            var endpoint = config.StorageConnection?.Url ?? entry.Url;
            return string.IsNullOrWhiteSpace(StorageConfig.Load(endpoint).Missing());
        }

        return !string.IsNullOrWhiteSpace(ClientConfig.GetSecret(entry.CredentialKey));
    }

    /// <summary>
    /// The plain-text list of what the client can switch to. Shown when there is no
    /// terminal to pick from.
    /// </summary>
    void ShowServers()
    {
        screen.Write("connections", ConsoleColor.White);

        foreach (var entry in config.AllConnections)
        {
            var active = string.Equals(entry.Name, config.ConnectionName, StringComparison.OrdinalIgnoreCase);
            var badge = HasCredential(entry) ? "key" : "no key";
            screen.Write($"  {(active ? "*" : " ")} {entry.Name,-16} {entry.Url,-46} {badge}",
                active ? ConsoleColor.Green : ConsoleColor.DarkGray);
        }

        screen.Write("");
        screen.Write("servers", ConsoleColor.White);

        if (config.Servers.Count == 0)
            screen.Write("  (none saved yet — /connect save <name> <url>)", ConsoleColor.DarkGray);
        else
        {
            foreach (var server in config.Servers)
            {
                var active = string.Equals(server.Name, config.ServerName, StringComparison.OrdinalIgnoreCase);
                screen.Write($"  {(active ? "*" : " ")} {server.Name,-16} {server.Url}",
                    active ? ConsoleColor.Green : ConsoleColor.DarkGray);
            }
        }

        screen.Write("");
        screen.Write($"  current: {config.ServerLabel} → {config.BaseUrl}", ConsoleColor.Cyan);
        screen.Write($"  model:   {config.Model}", ConsoleColor.Cyan);
        screen.Write("  /connect <name|url>   switch", ConsoleColor.DarkGray);
        screen.Write("  /connect key <name>  set the provider's key", ConsoleColor.DarkGray);
        screen.Write("  /connect save <name> <url>", ConsoleColor.DarkGray);
        screen.Write("  /connect forget <name>", ConsoleColor.DarkGray);
    }

    void SaveServer(string arguments)
    {
        var (name, url) = Split(arguments);
        if (name.Length == 0)
        {
            screen.Write("usage: /connect save <name> <url>", ConsoleColor.Red);
            return;
        }

        url = url.Length > 0 ? url : config.BaseUrl;

        var setting = Setting.Find("server");
        var error = setting?.Validate(url);
        if (error is not null)
        {
            screen.Write(error, ConsoleColor.Red);
            return;
        }

        setting!.Write(config, url);

        var existing = config.Servers.FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing is null) config.Servers.Add(new ServerEntry { Name = name, Url = config.BaseUrl });
        else existing.Url = config.BaseUrl;

        config.ServerName = name;
        config.Save();

        Wire.TraceId = null;
        Wire.Write("server.save", "{0} → {1}", name, config.BaseUrl);

        screen.Write($"saved {name} → {config.BaseUrl}", ConsoleColor.Green);
        screen.Write("switched to it", ConsoleColor.DarkGray);
        Reconnect();
    }

    void ForgetServer(string name)
    {
        if (name.Length == 0)
        {
            screen.Write("usage: /connect forget <name>", ConsoleColor.Red);
            return;
        }

        var existing = config.Servers.FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            screen.Write($"no server named '{name}'", ConsoleColor.Red);
            return;
        }

        config.Servers.Remove(existing);

        // Leaving the removed server selected would leave the client pointed at an
        // address it no longer knows how to label.
        if (string.Equals(config.ServerName, existing.Name, StringComparison.OrdinalIgnoreCase))
            config.ServerName = "";

        config.Save();
        Wire.TraceId = null;
        Wire.Write("server.forget", "{0}", existing.Name);
        screen.Write($"forgot {existing.Name}", ConsoleColor.Green);
    }

    string KnownServerList() =>
        config.Servers.Count == 0 ? "(none)" : string.Join(", ", config.Servers.Select(s => s.Name));

    string KnownConnectionList() =>
        string.Join(", ", config.AllConnections.Select(c => c.Name));

    /// <summary>
    /// The named connection in use, or null when the address was typed by hand and
    /// belongs to no named connection. The API client needs this to pick the right
    /// wire protocol, address and credential.
    /// </summary>
    ConnectionEntry? GetActiveConnection() => config.ActiveConnection;

/// <summary>
/// Changes how much of the agent's work runs unattended. Kept as its own command
/// rather than buried in /config because it is the one setting a person flips
/// mid-conversation: trust the task, run it, then turn it back off.
/// </summary>
void SetApprovals(string arguments)
{
    if (arguments.Length == 0)
    {
        PickApprovals();
        return;
    }

    // An unrecognised word must not land on Ask: silently tightening the barrier
    // is the worst possible reading of "yes".
    if (!Enum.TryParse<Approvals>(arguments.Trim(), true, out _) && !IsAffirmative(arguments))
    {
        screen.Write($"unknown approval mode '{arguments}'", ConsoleColor.Red, EntryStyle.Error);
        screen.Write("use ask, writes or all", ConsoleColor.DarkGray);
        return;
    }

    config.ApprovalMode = ToolRisk.Normalize(arguments);
    ApplyApprovals(config.Approvals);
}

/// <summary>
/// The no-argument form of /approve, also reached with Shift+Tab: a three-row list
/// so the middle mode can be picked without remembering its spelling.
/// </summary>
void PickApprovals()
{
    if (!screen.IsInteractive)
    {
        screen.Write($"  approvals  {config.Approvals}", ConsoleColor.Cyan);
        screen.Write($"  {ToolRisk.Describe(config.Approvals)}", ConsoleColor.DarkGray);
        screen.Write("  change it with /approve ask, /approve writes or /approve all", ConsoleColor.DarkGray);
        screen.Write($"  tools: {string.Join(", ", tools.Specs.Select(s => s.Name))}", ConsoleColor.DarkGray);
        screen.Write("  always asked in 'writes': " +
            string.Join(", ", tools.Specs.Select(s => s.Name).Where(ToolRisk.CanWrite)), ConsoleColor.DarkGray);
        return;
    }

    var modes = new[] { Approvals.Ask, Approvals.Writes, Approvals.All };
    var items = modes
        .Select(m => new PickItem(
            m.ToString().ToLowerInvariant(),
            ToolRisk.Describe(m),
            m == config.Approvals ? "current" : null,
            m == config.Approvals))
        .ToList();

    var picked = screen.Pick("approvals", items, "↑↓ move · ⏎ use mode · Esc close", (int)config.Approvals);
    if (picked is null) return;
    ApplyApprovals(modes[picked.Value]);
}

void ApplyApprovals(Approvals mode)
{
    config.ApprovalMode = mode.ToString();
    config.Save();
    RefreshHeader();

    Wire.TraceId = null;
    Wire.Write("approvals", "{0} · {1}", mode, ToolRisk.Describe(mode));

    screen.Write($"approvals = {mode.ToString().ToLowerInvariant()} · {ToolRisk.Describe(mode)}",
        ConsoleColor.Green, EntryStyle.Notice);
}

void CycleApprovals()
{
    var next = config.Approvals switch
    {
        Approvals.Ask => Approvals.Writes,
        Approvals.Writes => Approvals.All,
        _ => Approvals.Ask,
    };
    ApplyApprovals(next);
}

static bool IsAffirmative(string value) =>
    value.Trim() is "y" or "yes" or "on" or "auto" or "all" or "w" or "writes" or "write";

async Task SelectProjectAsync(string arguments)
{
    if (arguments.Length == 0)
    {
        await PickProjectAsync();
        return;
    }

    await SwitchProjectAsync(arguments);
}

/// <summary>
/// The no-argument form of /project: current, recent workspaces, then a path.
/// </summary>
async Task PickProjectAsync()
{
    if (!screen.IsInteractive)
    {
        screen.Write($"  current [{project.Root}]", ConsoleColor.DarkGray);
        screen.Write("  switch it with /project <path>", ConsoleColor.DarkGray);
        return;
    }

    var items = new List<PickItem>
    {
        new("(current)", project.Root, "active", true),
    };

    foreach (var path in config.RecentProjects)
    {
        if (string.Equals(path, project.Root, StringComparison.OrdinalIgnoreCase)) continue;
        items.Add(new PickItem(Banner.ProjectLabel(path), path));
    }

    var customAt = items.Count;
    items.Add(new PickItem("enter a path…", "switch to a folder that is not listed"));

    var picked = screen.Pick("project", items, "↑↓ move · ⏎ open · Esc close");
    if (picked is null || picked.Value == 0) return;

    if (picked.Value == customAt)
    {
        var answer = screen.ReadLine("path > ", project.Root);
        if (answer.Action == PromptAction.Exit || answer.Text.Length == 0) return;
        await SwitchProjectAsync(answer.Text);
        return;
    }

    await SwitchProjectAsync(items[picked.Value].Detail ?? items[picked.Value].Label);
}

async Task SwitchProjectAsync(string path)
{
    await Task.Yield();

    try
    {
        project = Project.Open(path);
        tools = new ToolRegistry(project.Root, config);
        log = new SessionLog(project.LogPath);
        _modeStore = new ModeStore(project);
        _modes = new ModeManager(project, _modeStore, screen);
        // A new workspace means a new conversation, so the remote chat behind this
        // one has to be released along with the transcript.
        history.Clear();
        sessionKey = NewSessionKey();
        config.Project = project.Root;
        RememberProject(project.Root);
        config.Save();
        RefreshHeader();
        screen.Clear();
        screen.Write($"project = {project.Root}", ConsoleColor.Green, EntryStyle.Notice);
    }
    catch (Exception ex)
    {
        screen.Write("error: " + ex.Message, ConsoleColor.Red, EntryStyle.Error);
    }
}

/// <summary>
/// The settings screen. Every change is validated and written immediately, so
/// there is no separate save step to forget and no way to lose an edit by leaving.
/// </summary>
async Task EditConfigAsync(string arguments)
{
    if (arguments.Length == 0)
    {
        if (screen.IsInteractive)
        {
            await OpenSettingsAsync();
            return;
        }

        PrintConfig();
        screen.Write("change one with /config <name> [value], or /config <name> to be asked", ConsoleColor.DarkGray);
        screen.Write("clear a value with '!', e.g. /config key !", ConsoleColor.DarkGray);
        return;
    }

    var (name, value) = Split(arguments);

    // /config reset [name] — back to a known state without hunting for the old value.
    if (name.Equals("reset", StringComparison.OrdinalIgnoreCase))
    {
        await ResetSettingsAsync(value);
        return;
    }

    var setting = Setting.Find(name);
    if (setting is null)
    {
        var suggestion = Setting.Suggest(name);
        screen.Write($"unknown setting '{name}'", ConsoleColor.Red);
        if (suggestion is not null) screen.Write($"did you mean '{suggestion}'?", ConsoleColor.Yellow);
        screen.Write("settings: " + string.Join(", ", Setting.All.Select(s => s.Name)), ConsoleColor.DarkGray);
        return;
    }

    if (value.Length == 0)
    {
        // No inline value: ask for one, showing what is there now.
        screen.Write($"  {setting.Name} — {setting.Hint}", ConsoleColor.DarkGray);
        screen.Write($"  now: {Trim(setting.Display(config), 160)}", ConsoleColor.DarkGray);
        if (setting.Clearable) screen.Write("  type ! to clear", ConsoleColor.DarkGray);

        value = screen.ReadLine($"{setting.Name} > ", Editable(setting, config)).Text;
        if (value.Length == 0) return;
    }

    await ApplySettingAsync(setting, value);
}

/// <summary>
/// The settings screen. Every change is validated and written immediately, so
/// there is no separate save step to forget and no way to lose an edit by leaving.
/// The list stays open after a change: adjusting server, model and approvals is
/// one visit rather than three commands.
/// </summary>
async Task OpenSettingsAsync()
{
    var selected = 0;
    while (true)
    {
        var items = Setting.All
            .Select(s => new PickItem(s.Name, s.Hint, Trim(s.Display(config), 34)))
            .ToList();
        items.Add(new PickItem("reset to defaults", "restore server, model, tokens, temperature and system prompt"));

        var picked = screen.Pick("settings", items, "↑↓ move · ⏎ edit · Esc close · changes save immediately", selected);
        if (picked is null) return;

        selected = picked.Value;
        if (selected >= Setting.All.Count)
        {
            await ResetSettingsAsync("");
            selected = 0;
            continue;
        }

        var setting = Setting.All[selected];

        // Lists beat typing for the two settings that only have a handful of
        // valid values. The settings screen stays open behind them.
        if (setting.Name == "model")
        {
            await ShowModelsAsync();
            continue;
        }
        if (setting.Name == "approvals")
        {
            PickApprovals();
            continue;
        }

        var answer = screen.ReadLine($"{setting.Name} > ", Editable(setting, config));
        if (answer.Action == PromptAction.Exit) return;
        if (answer.Text.Length == 0) continue;

        await ApplySettingAsync(setting, answer.Text);
    }
}

async Task ApplySettingAsync(Setting setting, string value)
{
    if (value == "!" && setting.Clearable)
    {
        setting.Write(config, "");
        Commit(setting, "(cleared)");
    }
    else
    {
        var error = setting.Validate(value);
        if (error is not null)
        {
            screen.Write(error, ConsoleColor.Red);
            screen.Write($"  {setting.Hint}", ConsoleColor.DarkGray);
            Wire.TraceId = null;
            Wire.Write("config.err", "{0} rejected: {1}", setting.Name, error);
            return;
        }

        setting.Write(config, value);
        Commit(setting, setting.Display(config));
    }

    if (!setting.NeedsReconnect) return;

    Reconnect();

    // A URL that does not parse is a valid URL that points nowhere. Say so here,
    // where the person can still do something about it, instead of failing on the
    // next message with a connection error.
    await CheckHealthAsync();
}

void Commit(Setting setting, string shown)
{
    config.Save();
    RefreshHeader();
    Wire.TraceId = null;
    Wire.Write("config", "{0} = {1}", setting.Name, shown);
    screen.Write($"{setting.Name} = {Trim(shown, 160)}", ConsoleColor.Green, EntryStyle.Notice);
    screen.Write("saved to " + ClientConfig.ConfigPath, ConsoleColor.DarkGray);
}

async Task ResetSettingsAsync(string name)
{
    if (name.Length == 0)
    {
        var fresh = new ClientConfig();
        config.BaseUrl = fresh.BaseUrl;
        config.Model = fresh.Model;
        config.System = fresh.System;
        config.MaxTokens = fresh.MaxTokens;
        config.Temperature = fresh.Temperature;
        // The key is left alone on purpose: silently dropping a working credential
        // is a worse outcome than leaving a stale one, and /config key ! clears it.
        config.Save();
        Reconnect();
        screen.Write("settings restored to defaults (key left as it was)", ConsoleColor.Green, EntryStyle.Notice);
        await CheckHealthAsync();
        return;
    }

    var setting = Setting.Find(name);
    if (setting is null)
    {
        screen.Write($"unknown setting '{name}'", ConsoleColor.Red);
        return;
    }

    setting.Write(config, setting.DefaultValue);
    Commit(setting, setting.Display(config));
    if (setting.NeedsReconnect)
    {
        Reconnect();
        await CheckHealthAsync();
    }
}

/// <summary>
/// The value the prompt starts with when editing a setting: the current one,
/// flattened to a single line, and blank for anything secret.
/// </summary>
static string Editable(Setting setting, ClientConfig config) =>
    setting.Secret ? "" : setting.Display(config).Replace('\r', ' ').Replace('\n', ' ');

static (string Name, string Value) Split(string arguments)
{
    var index = arguments.IndexOf(' ');
    return index < 0
        ? (arguments.Trim(), "")
        : (arguments[..index].Trim(), arguments[(index + 1)..].Trim());
}

/// <summary>
/// Rebuilds the HTTP client against the current settings. Deliberately does not
/// clear the transcript: changing the server address must not throw away the
/// conversation the user can still see.
/// </summary>
void Reconnect()
{
    api.Dispose();
    api = new ApiClient(config, GetActiveConnection());
    RefreshHeader();
}

/// <summary>
/// Переключение и просмотр режимов разработки: Architect → Code → Debug,
/// координирует Orchestrator. Состояние хранится в .IntellectHub/mode.json.
/// </summary>
void ModeCommandAsync(string arguments)
{
    var (name, rest) = Split(arguments);

    if (name.Length == 0 || name.Equals("status", StringComparison.OrdinalIgnoreCase))
    {
        ShowModeStatus();
        return;
    }

    if (name.Equals("reset", StringComparison.OrdinalIgnoreCase))
    {
        _modes!.ResetForNewTask();
        screen.Write("моды сброшены — следующая задача начнётся с архитектора (Architect Mode).",
            ConsoleColor.Green, EntryStyle.Notice);
        return;
    }

    if (name.Equals("next", StringComparison.OrdinalIgnoreCase))
    {
        _modes!.Complete(_modes.Effective);
        screen.Write($"следующий режим: {_modes.EffectiveInfo.Title}",
            ConsoleColor.Green, EntryStyle.Notice);
        return;
    }

    try
    {
        var mode = Modes.Parse(name);
        _modes!.SwitchTo(mode, "переключено пользователем");
    }
    catch (Exception ex)
    {
        screen.Write("error: " + ex.Message, ConsoleColor.Red, EntryStyle.Error);
    }
}

void ShowModeStatus()
{
    if (_modes is null)
    {
        screen.Write("моды недоступны", ConsoleColor.Red);
        return;
    }

    var s = _modes.State;
    screen.Write("режимы разработки (.IntellectHub)", ConsoleColor.White);
    screen.Write($"  текущий: {_modes.CurrentInfo.Title} ({_modes.CurrentDisplay}) — {_modes.CurrentInfo.Description}",
        ConsoleColor.Cyan);
    if (_modes.Effective != _modes.Current)
        screen.Write($"  рабочий (под капотом): {_modes.EffectiveInfo.Title} ({_modes.EffectiveDisplay})",
            ConsoleColor.DarkGray);
    screen.Write($"  архитектура готова: {(s.ArchitectureReady ? "да" : "нет")}");
    screen.Write($"  план готов: {(s.PlanReady ? "да" : "нет")}");
    screen.Write($"  код готов: {(s.CodeReady ? "да" : "нет")}");
    if (s.LastError is { } err) screen.Write($"  последняя ошибка: {err}", ConsoleColor.Red);
    screen.Write($"  циклов отладки: {s.DebugCycles}");
    screen.Write($"  последняя задача: {s.LastTask ?? "(нет)"}");
    if (s.History.Count > 0)
    {
        screen.Write("  история переходов: " +
            string.Join(" → ", s.History.Select(h => $"{h.From?.ToString() ?? "—"}→{h.To}")));
    }
    screen.Write("");
    screen.Write("  /mode architect · /mode code · /mode debug · /mode orchestrator", ConsoleColor.DarkGray);
    screen.Write("  /mode next · /mode reset · /mode status", ConsoleColor.DarkGray);
}

async Task PreviewCommandAsync(string arguments)
{
    var (path, rest) = Split(arguments);

    if (path.Equals("errors", StringComparison.OrdinalIgnoreCase))
    {
        await PreviewErrorsAsync();
        return;
    }

    var target = path.Length > 0 ? path : "index.html";

    var projectRoot = project?.Root ?? Directory.GetCurrentDirectory();
    var filePath = Path.GetFullPath(Path.Combine(projectRoot, target));

    if (!File.Exists(filePath))
    {
        screen.Write($"файл не найден: {filePath}", ConsoleColor.Red);
        screen.Write("доступные файлы: " + string.Join(", ", Directory.GetFiles(projectRoot, "*.html")
            .Select(f => Path.GetFileName(f))), ConsoleColor.DarkGray);
        return;
    }

    // Остановим старый сервер, если был.
    _previewServer?.Dispose();

    // Находим свободный порт и стартуем сервер.
    _previewServer = new PreviewServer(projectRoot, 0);
    _previewServer.Start();

    // Очищаем старые JS-ошибки.
    _previewServer.ClearErrors();

    var url = _previewServer.BaseUrl + Path.GetFileName(filePath);

    screen.Write($"preview сервер запущен на http://127.0.0.1:{_previewServer.Port}", ConsoleColor.Cyan);
    screen.Write($"открываю: {url}", ConsoleColor.Cyan);

    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
    catch (Exception ex)
    {
        screen.Write("не удалось открыть браузер автоматически: " + ex.Message, ConsoleColor.Red);
        screen.Write("откройте вручную: " + url, ConsoleColor.Yellow);
    }

    screen.Write("проверьте консоль браузера (F12 → Console) на наличие JS-ошибок.", ConsoleColor.DarkGray);
    screen.Write("команда /preview errors покажет ошибки, перехваченные сервером.", ConsoleColor.DarkGray);
}

async Task PreviewErrorsAsync()
{
    if (_previewServer is null)
    {
        screen.Write("preview сервер не запущен. Сначала выполните /preview", ConsoleColor.Red);
        return;
    }

    // Дадим серверу секунду накопить ошибки.
    await Task.Delay(1500);

    var errors = _previewServer.JsErrors;
    if (errors.Count == 0)
    {
        screen.Write("JS-ошибок не перехвачено.", ConsoleColor.Green);
        return;
    }

    screen.Write($"перехвачено JS-ошибок: {errors.Count}", ConsoleColor.Red);
    foreach (var err in errors.Take(10))
        screen.Write("  " + err, ConsoleColor.DarkGray);
}

async Task<bool> VerifyGameWorksViaPreview(string htmlFileName, string traceId)
{
    if (_previewServer is null)
    {
        // Сервер не запущен — не можем верифицировать, пропускаем проверку.
        return true;
    }

    var url = _previewServer.BaseUrl + htmlFileName;
    screen.Write($"верификация через preview: {url}", ConsoleColor.DarkGray);

    // Даём браузеру время загрузить страницу и выполнить скрипты.
    await Task.Delay(3000);

    var errors = _previewServer.JsErrors;
    if (errors.Count > 0)
    {
        screen.Write("верификация не пройдена: JS-ошибки в браузере", ConsoleColor.Red);
        foreach (var err in errors.Take(5))
            screen.Write("  " + err, ConsoleColor.DarkGray);
        return false;
    }

    screen.Write("верификация пройдена: JS-ошибок не обнаружено.", ConsoleColor.Green);
    return true;
}

async Task PlanCurrentTaskAsync()
{
    // Находим последнее сообщение пользователя в истории.
    var lastUserMsg = history.LastOrDefault(m => m.Role == "user");
    if (lastUserMsg is null)
    {
        screen.Write("Нет сообщений пользователя в истории.", ConsoleColor.Red);
        return;
    }

    var userText = lastUserMsg.Content?.ToString() ?? "";
    screen.Write("Прошу модель разбить задачу на подзадачи...", ConsoleColor.Yellow);

    var planPrompt = new ChatMessage
    {
        Role = "user",
        Content = JsonValue.Create(
            "Разбей эту задачу на подзадачи и верни JSON-план:\n" +
            "{\"subtasks\":[{\"title\":\"...\",\"description\":\"...\"}, ...]}\n\n" +
            "Задача пользователя:\n" + userText + "\n\n" +
            "Каждая подзадача — отдельный шаг, который клиент будет выполнять по очереди.\n" +
            "Не пытайся выполнить задачу за один ответ — только план.")
    };
    history.Add(planPrompt);

    try
    {
        await RunTurnWithInputAsync();
    }
    catch (OperationCanceledException)
    {
        screen.Write("interrupted", ConsoleColor.DarkGray, EntryStyle.Notice);
        Wire.Write("turn.cancelled", "stopped by the user");
    }
    catch (Exception ex)
    {
        Sound.Error();
        screen.Write("error: " + ex.Message, ConsoleColor.Red, EntryStyle.Error);
        Wire.Write("turn.err", "{0}: {1}", ex.GetType().Name, ex.Message);
    }
}

void PrintConfig()
{
    foreach (var setting in Setting.All)
    {
        screen.Write($"  {setting.Name,-12} {setting.Display(config)}");
        screen.Write($"  {"",-12} {setting.Hint}", ConsoleColor.DarkGray);
    }

    screen.Write($"  {"project",-12} {project.Root}");
    screen.Write("");
    screen.Write("  stored in " + ClientConfig.ConfigPath, ConsoleColor.DarkGray);
}

void ShowHelp()
{
    // Generated from the same table the parser and the sidebar use, so the help
    // can never describe a command that stopped working.
    screen.Write("commands", ConsoleColor.White);
    foreach (var command in CommandLine.Known.Where(c => !c.Hidden))
    {
        screen.Write($"  {command.Usage}", ConsoleColor.Cyan);
        screen.Write($"    {command.Summary}", ConsoleColor.DarkGray);
        if (command.Aliases is { Length: > 0 })
            screen.Write($"    also: {string.Join(", ", command.Aliases)}", ConsoleColor.DarkGray);
    }

    screen.Write("");
    screen.Write("shortcuts", ConsoleColor.White);
    screen.Write("  ⌃K  command palette        ⌃O  settings", ConsoleColor.Cyan);
    screen.Write("  F1  this help              ⌃L  clear conversation", ConsoleColor.Cyan);
    screen.Write("  ⇧⇥  cycle approvals        ⌃B  show/hide side rail", ConsoleColor.Cyan);
    screen.Write("  ⌃T  tool output on/off     ⌃C  clear input, then exit", ConsoleColor.Cyan);
    screen.Write("  ↑↓  history                Tab complete commands and values", ConsoleColor.Cyan);
    screen.Write("  PgUp/PgDn scroll           Esc clear input", ConsoleColor.Cyan);

    screen.Write("");
    screen.Write("typed without a value, these open a list: /config /models /connect /project /approve",
        ConsoleColor.DarkGray);
    screen.Write("settings: " + string.Join(", ", Setting.All.Select(s => s.Name)), ConsoleColor.DarkGray);
    screen.Write("tools: " + string.Join(", ", tools.Specs.Select(s => s.Name)), ConsoleColor.DarkGray);
    screen.Write("barrier: [⏎] run  [s] step  [e] edit  [q] stop", ConsoleColor.DarkGray);
}

/// <summary>
/// Проверяет, является ли имя инструмента placeholder-именем (отказом от вызова).
/// Совпадает со списком в серверном ToolCallParser.IsPlaceholderName.
/// </summary>
static bool IsPlaceholderToolName(string name)
{
    var token = name.Trim().Trim('"', '\'');
    if (token.Length == 0) return true;
    return token.Equals("none", StringComparison.OrdinalIgnoreCase) ||
           token.Equals("null", StringComparison.OrdinalIgnoreCase) ||
           token.Equals("nil", StringComparison.OrdinalIgnoreCase) ||
           token.Equals("no_tool", StringComparison.OrdinalIgnoreCase) ||
           token.Equals("no_tool_available", StringComparison.OrdinalIgnoreCase) ||
           token.Equals("no_tools_available", StringComparison.OrdinalIgnoreCase) ||
           token.Equals("no-op", StringComparison.OrdinalIgnoreCase) ||
           token.Equals("noop", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Если модель в Auto/Architect ответила текстом (архитектура + план), не
/// выбрасываем этот ответ и не ждём пользователя: сохраняем его в
/// .IntellectHub/architecture.md и plan.md и переключаемся на Code.
/// Возвращает true, если текст удалось принять как артефакт.
/// </summary>
bool TryAdoptArchitectReply(string text)
{
    if (_modes is null || _modeStore is null) return false;
    if (_modes.Effective != Mode.Architect) return false;

    var architecture = text.Trim();
    var anyReady = _modes.State.ArchitectureReady || _modes.State.PlanReady;

    // Ничего не записано и текста нет/он слишком короткий — принять нечего.
    if (!anyReady && architecture.Length < 40) return false;

    try
    {
        if (!_modes.State.ArchitectureReady && !_modes.State.PlanReady)
        {
            // Модель ответила прозой: полный текст — архитектура, секция «План» — план.
            _modes.RecordArtifact(Mode.Architect, ".IntellectHub/architecture.md", architecture);
            _modeStore.SavePlan(ExtractPlanSection(architecture), _modes.State);
        }
        else
        {
            // Один артефакт уже записан инструментами. Не перезаписываем его
            // финальным отчётом — только дополняем недостающий.
            if (!_modes.State.ArchitectureReady)
            {
                var planText = _modeStore.Read(_modeStore.PlanPath);
                if (string.IsNullOrWhiteSpace(planText)) return false;
                _modes.RecordArtifact(Mode.Architect, ".IntellectHub/architecture.md", planText);
            }

            if (!_modes.State.PlanReady)
            {
                var archText = _modeStore.Read(_modeStore.ArchitecturePath);
                var planSource = string.IsNullOrWhiteSpace(archText) ? architecture : archText;
                if (string.IsNullOrWhiteSpace(planSource)) return false;
                _modeStore.SavePlan(ExtractPlanSection(planSource), _modes.State);
            }
        }
    }
    catch (Exception ex)
    {
        Wire.Write("mode.adopt_failed", "{0}: {1}", ex.GetType().Name, ex.Message);
        return false;
    }

    if (!_modes.State.ArchitectureReady || !_modes.State.PlanReady) return false;

    _modes.AdvanceIfReady();
    screen.Write("Архитектура и план сохранены в .IntellectHub — перехожу к коду.", ConsoleColor.Yellow);
    Wire.Write("mode.architect_text", "architecture.md/plan.md ready · effective={0}", _modes.Effective);
    return true;
}

/// <summary>
/// Достаёт из текста секцию «План»/«Plan», если она есть. Иначе весь текст
/// становится планом — лучше сохранить ответ модели, чем потерять его.
/// </summary>
static string ExtractPlanSection(string architecture)
{
    var lines = architecture.Replace("\r\n", "\n").Split('\n');
    var start = -1;
    for (var i = 0; i < lines.Length; i++)
    {
        var line = lines[i].TrimStart();
        if (line.StartsWith("#") &&
            (line.Contains("plan", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("план", StringComparison.OrdinalIgnoreCase)))
        {
            start = i;
            break;
        }
    }

    var body = start >= 0
        ? string.Join("\n", lines.Skip(start)).Trim()
        : architecture;
    return "# План реализации\n\n" + body;
}

/// <summary>
/// Self-check: проверяет, использовала ли модель инструменты write/edit в текущем или предыдущих шагах.
/// </summary>
bool HasUsedWriteTools()
{
    return _hasUsedWriteTools;
}

/// <summary>
/// Проверяет, что все файлы, которые модель записала в текущей подзадаче,
/// реально существуют на диске и не пустые. Если файлов нет — подзадача не считается выполненной.
/// </summary>
bool VerifyWrittenFiles()
{
    if (_writtenFiles.Count == 0) return true;

    foreach (var relPath in _writtenFiles)
    {
        var fullPath = Path.GetFullPath(Path.Combine(project.Root, relPath));
        if (!File.Exists(fullPath))
        {
            Wire.Write("verify.files", "missing file: {0}", fullPath);
            return false;
        }
        try
        {
            if (new FileInfo(fullPath).Length == 0)
            {
                Wire.Write("verify.files", "empty file: {0}", fullPath);
                return false;
            }
        }
        catch
        {
            return false;
        }
    }
    return true;
}

    /// <summary>
    /// Проверяет, что модель использовала write/edit для текущей подзадачи,
    /// верифицирует, что файлы реально существуют на диске, и помечает
    /// подзадачу как выполненную. Возвращает false, если нужен repeat-запрос
    /// (файлы не найдены и попытки не исчерпаны).
    /// </summary>
    async Task<bool> TryCompleteCurrentSubtask(string assistantText, string traceId, List<ChatMessage> history, bool verifyBrowser = true)
    {
        // Планировщик мог уже перейти за последнюю подзадачу (например, после
        // предыдущей проверки). Завершать больше нечего.
        if (_planner?.Current is null)
            return true;

        // Сначала проверяем файлы. Если их нет — браузер проверять бессмысленно.
        var filesOk = VerifyWrittenFiles();
        var browserOk = true;
        if (filesOk && verifyBrowser)
        {
            var htmlFile = _writtenFiles.FirstOrDefault(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
            if (htmlFile is null && File.Exists(Path.Combine(project.Root, "index.html")))
                htmlFile = "index.html";

            // На ранних подзадачах (архитектура, стили, скрипт) HTML может ещё
            // не существовать. Проверять нечего — не превращаем это в retry.
            if (htmlFile is not null)
                browserOk = await VerifyGameWorksViaPreview(htmlFile, traceId);
        }

        // Обе проверки выполняются ДО завершения подзадачи. Раньше helper вызывался
        // дважды: первый вызов помечал подзадачу выполненной и сдвигал планировщик,
        // второй — помечал выполненной уже следующую (или падал на null в конце).
        // Из-за этого пропускалась каждая вторая подзадача, а на последней
        // возникал NullReferenceException.
        var result = SubtaskHelpers.TryCompleteSubtask(
            _planner!,
            screen,
            traceId,
            history,
            filesOk && browserOk,
            filesOk ? "ошибки в браузере" : "файлы не созданы",
            filesOk
                ? SubtaskHelpers.BrowserErrorsRetryFactory(_planner!)
                : SubtaskHelpers.FilesMissingRetryFactory(_planner!),
            "инструменты использованы, файлы проверены, браузер ОК");

        return result != SubtaskCompletionResult.Retry;
    }

static class Sound
{
    private static readonly bool _isWindows = OperatingSystem.IsWindows();
    
    // Кэшируем делегаты для воспроизведения звуков на Windows, чтобы избежать reflection на каждый вызов
    private static readonly Action? _playAsterisk;
    private static readonly Action? _playBeep;
    private static readonly Action? _playHand;

    static Sound()
    {
        if (_isWindows)
        {
            try
            {
                var asm = Assembly.Load("System.Media");
                var type = asm.GetType("System.Media.SystemSounds")!;
                var asteriskProp = type.GetProperty("Asterisk", BindingFlags.Public | BindingFlags.Static)!;
                var beepProp = type.GetProperty("Beep", BindingFlags.Public | BindingFlags.Static)!;
                var handProp = type.GetProperty("Hand", BindingFlags.Public | BindingFlags.Static)!;

                var asteriskSound = asteriskProp.GetValue(null)!;
                var beepSound = beepProp.GetValue(null)!;
                var handSound = handProp.GetValue(null)!;

                var playMethod = asteriskSound.GetType().GetMethod("Play", BindingFlags.Public | BindingFlags.Instance)!;

                _playAsterisk = () => playMethod.Invoke(asteriskSound, null);
                _playBeep = () => playMethod.Invoke(beepSound, null);
                _playHand = () => playMethod.Invoke(handSound, null);
            }
            catch
            {
                // Игнорируем ошибки инициализации, используем fallback
            }
        }
    }

    public static void Notify() => Play(_playAsterisk);
    public static void Success() => Play(_playBeep);
    public static void Error() => Play(_playHand);

    private static void Play(Action? playAction)
    {
        if (!_isWindows || playAction is null) { Console.Beep(); return; }
        try { playAction(); } catch { Console.Beep(); }
    }
}
    

