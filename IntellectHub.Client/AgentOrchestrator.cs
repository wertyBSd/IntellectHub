using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IntellectHub.Client;

/// <summary>
/// Orchestrates the agent's turn execution loop.
/// Extracted from Program.cs to separate the agent logic from the REPL shell.
/// </summary>
internal sealed class AgentOrchestrator
{
    private readonly TurnContext _ctx;
    private GitCheckpoint.CheckpointResult? _lastCheckpoint;
    private BuildValidator? _buildValidator;
    private CircuitBreaker _circuitBreaker;
    private McpManager? _mcpManager;

    public AgentOrchestrator(TurnContext ctx)
    {
        _ctx = ctx;
        _circuitBreaker = new CircuitBreaker();
    }

    public void InitializeBuildValidator()
    {
        if (!string.IsNullOrWhiteSpace(_ctx.Config.BuildCommand))
        {
            _buildValidator = new BuildValidator(
                _ctx.Config,
                _ctx.Project.Root,
                _ctx.Screen,
                _ctx.Log);
        }
    }

    public void InitializeMcpManager()
    {
        if (_mcpManager != null) return;
        
        _mcpManager = new McpManager(_ctx.Screen);
        
        // Auto-connect configured MCP servers
        foreach (var (name, config) in _ctx.Config.McpServers)
        {
            if (config.AutoConnect)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _mcpManager!.ConnectAsync(name, config.Command, config.Args.ToArray(), config.Env);
                    }
                    catch (Exception ex)
                    {
                        Wire.Write("mcp.auto_connect_failed", "server={0} error={1}", name, ex.Message);
                    }
                });
            }
        }
    }

    internal McpManager? McpManager => _mcpManager;

    /// <summary>
    /// Executes a full turn with the given user input.
    /// Returns true if the conversation should continue, false if it should exit.
    /// </summary>
    public async Task<bool> RunTurnAsync(string userInput, CancellationToken ct)
    {
        _ctx.BeginTurn(Wire.NewTraceId());
        _ctx.History.Add(new ChatMessage { Role = "user", Content = JsonValue.Create(userInput) });
        _ctx.Log.User(userInput);

        InitializeBuildValidator();
        InitializeMcpManager();

        try
        {
            // Create checkpoint before starting the turn
            _lastCheckpoint = GitCheckpoint.CreateCheckpoint(_ctx.Project.Root, $"Turn: {userInput[..Math.Min(50, userInput.Length)]}");
            
            await RunTurnCoreAsync(ct);
            _ctx.Modes.AdvanceIfReady();
            return true;
        }
        catch (OperationCanceledException)
        {
            _ctx.Screen.Write("interrupted", ConsoleColor.DarkGray, EntryStyle.Notice);
            Wire.Write("turn.cancelled", "stopped by the user");
            return true;
        }
        catch (Exception ex)
        {
            Sound.Error();
            _ctx.Screen.Write("error: " + ex.Message, ConsoleColor.Red, EntryStyle.Error);
            Wire.Write("turn.err", "{0}: {1}", ex.GetType().Name, ex.Message);
            Wire.Block("turn.err.st", ex.ToString());
            return true;
        }
        finally
        {
            _ctx.Screen.Idle();
            UpdateUsage();
        }
    }

    private async Task RunTurnCoreAsync(CancellationToken ct)
    {
        AssistantReply? reply = null;

        Wire.Write("turn", "start · history={0} msgs · tools={1} · planner={2} · mode={3}",
            _ctx.History.Count, _ctx.Tools.Specs.Count,
            _ctx.Planner is not null ? "active" : "none",
            _ctx.Modes is null ? "-" : _ctx.Modes.CurrentDisplay);

        if (_ctx.Modes is not null)
        {
            var lastUser = _ctx.History.LastOrDefault(m => m.Role == "user")?.Content?.ToString() ?? "";
            _ctx.Modes.StartTurn(lastUser);
        }

        for (var step = 0; step < 16; step++)
        {
            _ctx.SetStep(step);
            ct.ThrowIfCancellationRequested();
            _ctx.Screen.Busy(_ctx.Step == 0 ? "thinking" : "working");

            var traceId = $"{_ctx.TurnId}.s{_ctx.Step}";
            Wire.TraceId = traceId;
            Wire.Write("step", "{0} of 16 · history={1} msgs", _ctx.Step, _ctx.History.Count);

            if (_ctx.Planner is not null && _ctx.Planner.HasNext)
            {
                await ExecuteSubtaskAsync(traceId, ct);
                if (_ctx.StopRequested) return;
                // Next iteration will execute subtask - use Coding role
                reply = await CallApiWithRetriesAsync(traceId, ct, ModelRole.Coding);
                if (reply is null) return;
                await ProcessReplyAsync(reply, traceId, ct);
                if (_ctx.StopRequested) return;
                continue;
            }

            reply = await CallApiWithRetriesAsync(traceId, ct, ModelRole.Default);
            if (reply is null) return;

            await ProcessReplyAsync(reply, traceId, ct);
            if (_ctx.StopRequested) return;

            if (_ctx.Planner is null && !reply.HasToolCalls)
            {
                await HandleFinalResponseAsync(reply, traceId);
                return;
            }
        }

        _ctx.Screen.Write("(stopped after 16 tool steps)", ConsoleColor.DarkGray);
        Wire.TraceId = _ctx.TurnId;
        Wire.Write("turn", "hit the 16-step cap without a final answer");
        Wire.Timing("turn.end", _ctx.TurnClock, "finished");
    }

    private async Task HandleFinalResponseAsync(AssistantReply reply, string traceId)
    {
        if (reply.Text.Trim().Length > 0)
            await RevealAsync(reply.Text.Trim());
        _ctx.History.Add(new ChatMessage { Role = "assistant", Content = JsonValue.Create(reply.Text) });

        await FinishTurnAsync(traceId, "no tool calls");
    }

    private async Task ExecuteSubtaskAsync(string traceId, CancellationToken ct)
    {
        var subtask = _ctx.Planner!.Current!;
        var subtaskPrompt = new ChatMessage
        {
            Role = "user",
            Content = JsonValue.Create(
                $"ПЕРВЫМ ДЕЛОМ просмотри существующие файлы проекта на диске (index.html, script.js, styles.css и т.д.) " +
                $"— посмотри, что уже готово, что сломано, чего не хватает. " +
                $"НЕ пиши рабочие файлы в .IntellectHub/ — пиши в корень проекта. " +
                $"Не дублируй уже существующий код.\n\n" +
                $"Выполни подзадачу {subtask.Index + 1} из {_ctx.Planner.Total}: {subtask.Title}\n\n" +
                $"Описание: {subtask.Description}\n\n" +
                $"ИСПОЛЬЗУЙ ИНСТРУМЕНТЫ write/edit, чтобы записать свою часть результата в соответствующие файлы проекта. " +
                $"НЕ ВЫВОДИ ТЕКСТ В ОТВЕТЕ — используй инструменты для записи в файл. " +
                $"НЕ ПИШИ СКРИПТЫ В ОТВЕТЕ — используй инструмент bash для выполнения команд. " +
                $"Не пытайся сделать всё за один вызов — пиши только эту часть. " +
                $"Когда закончишь, напиши краткий отчёт текстом без вызовов инструментов.")
        };
        _ctx.History.Add(subtaskPrompt);
        _ctx.ResetForNewSubtask();
        Wire.Write("planner.subtask", "executing subtask {0}/{1}: {2}", subtask.Index + 1, _ctx.Planner.Total, subtask.Title);
    }

    private async Task<AssistantReply?> CallApiWithRetriesAsync(string traceId, CancellationToken ct, ModelRole role = ModelRole.Default)
    {
        var callClock = Stopwatch.StartNew();
        const int maxRetries = 3;
        Exception? lastTransient = null;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Prune history to fit context window before sending to API
                var prunedHistory = ContextPruner.PruneHistory(
                    _ctx.Modes?.WithContext(_ctx.History) ?? _ctx.History,
                    _ctx.Config.Model);

                var reply = await _ctx.Api.SendAsync(
                    prunedHistory,
                    _ctx.Tools.Specs,
                    traceId,
                    _ctx.SessionKey,
                    role,
                    ct);

                Sound.Notify();
                Wire.Timing("step", callClock, "round trip finished · stop_reason={0}", reply.StopReason);
                return reply;
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

        return null;
    }

    private async Task ProcessReplyAsync(AssistantReply reply, string traceId, CancellationToken ct)
    {
        var allPlaceholder = reply.ToolCalls.All(c => IsPlaceholderToolName(c.Name));

        // Don't log/display the model's text response when it has actual tool calls.
        // The model often outputs the tool call as JSON in its text response (explaining what it's doing),
        // which clutters the console. The tool calls themselves are the action.
        if (reply.Text.Trim().Length > 0 && !reply.HasToolCalls)
            _ctx.Log.Thought(reply.Text.Trim());

        if (_ctx.Planner is null && allPlaceholder && string.IsNullOrWhiteSpace(reply.Text.Trim()))
        {
            await RequestPlanAsync(traceId);
            // Next iteration will request plan from model - use Planning role
            reply = await CallApiWithRetriesAsync(traceId, ct, ModelRole.Planning);
            if (reply is null) return;
            await ProcessReplyAsync(reply, traceId, ct);
            if (_ctx.StopRequested) return;
            return;
        }

        if (_ctx.Planner is null && !reply.HasToolCalls)
        {
            var maybePlan = TryExtractPlan(reply.Text);
            if (maybePlan is not null)
            {
                await LoadPlanAsync(maybePlan, reply, traceId);
                return;
            }
        }

        if (!reply.HasToolCalls || allPlaceholder)
        {
            await HandleTextOnlyResponseAsync(reply, traceId);
            return;
        }

        await ExecuteToolCallsAsync(reply, traceId, ct);
    }

    private async Task RequestPlanAsync(string traceId)
    {
        _ctx.Screen.Write("Модель не смогла выполнить задачу за один ход — предлагаю разбить на подзадачи.", ConsoleColor.Yellow);
        var planPrompt = new ChatMessage
        {
            Role = "user",
            Content = JsonValue.Create(
                "Ты не можешь выполнить эту задачу за один ответ, потому что она слишком большая. " +
                "Разбей её на подзадачи и верни JSON-план:\n" +
                "{\"subtasks\":[{\"title\":\"...\",\"description\":\"...\"}, ...]}\n\n" +
                "Каждая подзадача — это отдельный шаг, который клиент будет выполнять по очереди.")
        };
        _ctx.History.Add(planPrompt);
        Wire.TraceId = _ctx.TurnId;
        Wire.Write("turn", "noop received · asking model for decomposition plan");
    }

    private async Task LoadPlanAsync(TaskPlanner planner, AssistantReply reply, string traceId)
    {
        _ctx.SetPlanner(planner);
        _ctx.Screen.Write("План принят. Подзадач: " + _ctx.Planner.Total, ConsoleColor.Yellow);
        _ctx.Screen.Write(_ctx.Planner.PlanSummary(), ConsoleColor.DarkGray);

        var planBuilder = new System.Text.StringBuilder();
        planBuilder.AppendLine("# План реализации");
        planBuilder.AppendLine();
        planBuilder.AppendLine("Автоматически разбит на подзадачи клиентом.");
        planBuilder.AppendLine();
        foreach (var s in _ctx.Planner.Subtasks)
            planBuilder.AppendLine($"- [{s.Status}] {s.Index + 1}. {s.Title} — {s.Description}");
        _ctx.Modes.SavePlan(planBuilder.ToString(), _ctx.Modes.State);

        var assistantContent = new JsonArray();
        if (reply.Text.Trim().Length > 0)
            assistantContent.Add(new JsonObject { ["type"] = "text", ["text"] = reply.Text });
        _ctx.History.Add(new ChatMessage { Role = "assistant", Content = assistantContent });

        Wire.TraceId = _ctx.TurnId;
        Wire.Write("turn", "plan loaded · {0} subtasks · saved to .IntellectHub/plan.md · continuing with subtask execution", _ctx.Planner.Total);
    }

    private async Task HandleTextOnlyResponseAsync(AssistantReply reply, string traceId)
    {
        if (_ctx.Planner is null && string.IsNullOrWhiteSpace(reply.Text.Trim()))
        {
            await RequestPlanAsync(traceId);
            return;
        }

        if (_ctx.Planner is not null)
        {
            await HandleSubtaskTextResponseAsync(reply, traceId);
            return;
        }

        if (reply.Text.Trim().Length > 0)
            await RevealAsync(reply.Text.Trim());
        _ctx.History.Add(new ChatMessage { Role = "assistant", Content = JsonValue.Create(reply.Text) });

        await FinishTurnAsync(traceId, "no tool calls");
    }

    private async Task HandleSubtaskTextResponseAsync(AssistantReply reply, string traceId)
    {
        var currentSubtask = _ctx.Planner!.Current!;
        currentSubtask.Attempts++;

        if (string.IsNullOrWhiteSpace(reply.Text.Trim()) || reply.ToolCalls.All(c => IsPlaceholderToolName(c.Name)))
        {
            await HandleEmptySubtaskResponseAsync(currentSubtask, traceId);
            return;
        }

        if (_ctx.Config.SelfCheck && !_ctx.HasUsedWriteTools)
        {
            await HandleNoToolsUsedAsync(currentSubtask, traceId);
            return;
        }

        if (!_ctx.VerifyWrittenFiles())
        {
            await HandleMissingFilesAsync(currentSubtask, traceId);
            return;
        }

        await CompleteSubtaskAsync(currentSubtask, reply.Text.Trim(), traceId);
    }

    private async Task HandleEmptySubtaskResponseAsync(Subtask currentSubtask, string traceId)
    {
        const int maxAttempts = 3;
        if (currentSubtask.Attempts >= maxAttempts)
        {
            await FailSubtaskAsync(currentSubtask, "Model returned empty or placeholder responses too many times.", traceId);
            return;
        }

        _ctx.Screen.Write($"Подзадача {currentSubtask.Index + 1} не выполнена — модель не прислала содержимое, повторяем запрос (попытка {currentSubtask.Attempts}/{maxAttempts}).", ConsoleColor.Yellow);
        var retryPrompt = new ChatMessage
        {
            Role = "user",
            Content = JsonValue.Create(
                $"Ты должен написать содержимое подзадачи {currentSubtask.Index + 1} из {_ctx.Planner.Total}: \"{currentSubtask.Title}\".\n\n" +
                $"Описание: {currentSubtask.Description}\n\n" +
                $"Напиши текст раздела целиком, используя инструмент write/edit. Не присылай пустой ответ и не отказывайся от инструментов.")
        };
        _ctx.History.Add(retryPrompt);
        Wire.TraceId = _ctx.TurnId;
        Wire.Write("turn", "subtask {0} empty · retrying with stricter prompt (attempt {1}/{2})", currentSubtask.Index + 1, currentSubtask.Attempts, maxAttempts);
    }

    private async Task HandleNoToolsUsedAsync(Subtask currentSubtask, string traceId)
    {
        const int maxAttempts = 3;
        if (currentSubtask.Attempts >= maxAttempts)
        {
            await FailSubtaskAsync(currentSubtask, "Model did not use write/edit tools after multiple attempts.", traceId);
            return;
        }

        _ctx.Screen.Write($"Подзадача {currentSubtask.Index + 1}: модель не использовала инструменты — повторяем запрос (попытка {currentSubtask.Attempts}/{maxAttempts}).", ConsoleColor.Yellow);
        var retryPrompt = new ChatMessage
        {
            Role = "user",
            Content = JsonValue.Create(
                $"Ты должен написать содержимое подзадачи {currentSubtask.Index + 1} из {_ctx.Planner.Total}: \"{currentSubtask.Title}\".\n\n" +
                $"Описание: {currentSubtask.Description}\n\n" +
                $"ИСПОЛЬЗУЙ ИНСТРУМЕНТЫ write/edit, чтобы записать содержимое в соответствующие файлы проекта. " +
                $"НЕ ВЫВОДИ ТЕКСТ В ОТВЕТЕ — используй инструменты для записи в файл. " +
                $"НЕ ПИШИ СКРИПТЫ В ОТВЕТЕ — используй инструмент bash для выполнения команд.")
        };
        _ctx.History.Add(retryPrompt);
        Wire.TraceId = _ctx.TurnId;
        Wire.Write("turn", "subtask {0} no write tools · retrying (attempt {1}/{2})", currentSubtask.Index + 1, currentSubtask.Attempts, maxAttempts);
    }

    private async Task HandleMissingFilesAsync(Subtask currentSubtask, string traceId)
    {
        const int maxAttempts = 3;
        if (currentSubtask.Attempts >= maxAttempts)
        {
            await FailSubtaskAsync(currentSubtask, "Written files do not exist after multiple attempts.", traceId);
            return;
        }

        _ctx.Screen.Write($"Подзадача {currentSubtask.Index + 1}: файлы не найдены на диске, повторяем (попытка {currentSubtask.Attempts}/{maxAttempts}).", ConsoleColor.Yellow);
        var retryPrompt = SubtaskHelpers.CreateFilesMissingRetryPrompt(_ctx.Planner!, currentSubtask.Attempts, maxAttempts);
        _ctx.History.Add(retryPrompt);
        Wire.TraceId = _ctx.TurnId;
        Wire.Write("turn", "subtask {0} files missing · retrying (attempt {1}/{2})", currentSubtask.Index + 1, currentSubtask.Attempts, maxAttempts);
    }

    private async Task CompleteSubtaskAsync(Subtask currentSubtask, string assistantText, string traceId)
    {
        var htmlFile = _ctx.WrittenFiles.FirstOrDefault(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) ?? "index.html";
        var browserOk = await VerifyGameWorksViaPreviewAsync(htmlFile, traceId);

        if (!browserOk)
        {
            await HandleBrowserErrorsAsync(currentSubtask, traceId);
            return;
        }

        _ctx.MarkSubtaskDone(assistantText);
        _ctx.Screen.Write($"Подзадача {currentSubtask.Index + 1} готова (инструменты использованы, файлы проверены, браузер ОК).", ConsoleColor.Green);

        var assistantContent = new JsonArray();
        if (assistantText.Length > 0)
            assistantContent.Add(new JsonObject { ["type"] = "text", ["text"] = assistantText });
        _ctx.History.Add(new ChatMessage { Role = "assistant", Content = assistantContent });

        await CheckSubtaskCompletionAsync(traceId);
    }

    private async Task HandleBrowserErrorsAsync(Subtask currentSubtask, string traceId)
    {
        const int maxAttempts = 3;
        if (currentSubtask.Attempts >= maxAttempts)
        {
            await FailSubtaskAsync(currentSubtask, "JS errors in browser preview after multiple attempts.", traceId);
            return;
        }

        _ctx.Screen.Write($"Подзадача {currentSubtask.Index + 1}: ошибки в браузере, повторяем (попытка {currentSubtask.Attempts}/{maxAttempts}).", ConsoleColor.Yellow);
        var retryPrompt = SubtaskHelpers.CreateBrowserErrorsRetryPrompt(_ctx.Planner!, currentSubtask.Attempts, maxAttempts);
        _ctx.History.Add(retryPrompt);
        Wire.TraceId = _ctx.TurnId;
        Wire.Write("turn", "subtask {0} browser errors · retrying (attempt {1}/{2})", currentSubtask.Index + 1, currentSubtask.Attempts, maxAttempts);
    }

    private async Task FailSubtaskAsync(Subtask currentSubtask, string reason, string traceId)
    {
        _ctx.Screen.Write($"Подзадача {currentSubtask.Index + 1} не выполнена после 3 попыток — пропускаем.", ConsoleColor.Red);
        _ctx.MarkSubtaskFailed(reason);
        Wire.TraceId = _ctx.TurnId;
        Wire.Write("turn", "subtask {0} failed after 3 attempts · skipping", currentSubtask.Index + 1);

        var failContent = new JsonArray();
        failContent.Add(new JsonObject { ["type"] = "text", ["text"] = $"[Подзадача {currentSubtask.Index + 1} не выполнена — {reason}]" });
        _ctx.History.Add(new ChatMessage { Role = "assistant", Content = failContent });

        await CheckSubtaskCompletionAsync(traceId);
    }

    private async Task CheckSubtaskCompletionAsync(string traceId)
    {
        if (_ctx.Planner is not null && _ctx.Planner.HasNext)
        {
            Wire.TraceId = _ctx.TurnId;
            Wire.Write("turn", "subtask done · continuing with subtask {0}/{1}", _ctx.Planner.Current!.Index + 1, _ctx.Planner.Total);
            _ctx.AdvanceSubtask();
            return;
        }

        if (_ctx.Planner is not null && !_ctx.Planner.HasNext)
        {
            _ctx.Screen.Write("Все подзадачи выполнены.", ConsoleColor.Green);
            _ctx.SetPlanner(null);
            _ctx.Modes.AdvanceToNextMode();
            _ctx.RequestStop();

            Wire.TraceId = _ctx.TurnId;
            Wire.Write("turn", "done · all subtasks complete · {0} steps · {1} msgs in history", _ctx.Step + 1, _ctx.History.Count);
            Wire.Timing("turn.end", _ctx.TurnClock, "finished");
        }
    }

    private async Task ExecuteToolCallsAsync(AssistantReply reply, string traceId, CancellationToken ct)
    {
        var assistantContent = new JsonArray();
        if (reply.Text.Trim().Length > 0)
            assistantContent.Add(new JsonObject { ["type"] = "text", ["text"] = reply.Text });

        foreach (var call in reply.ToolCalls)
        {
            assistantContent.Add(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = call.Id,
                ["name"] = call.Name,
                ["input"] = JsonNode.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments),
            });
        }
        _ctx.History.Add(new ChatMessage { Role = "assistant", Content = (JsonNode)assistantContent });

        if (_ctx.Planner is not null && _ctx.Planner.HasNext && _ctx.Config.SelfCheck && _ctx.HasUsedWriteTools)
        {
            // Run build validation if configured and files were modified
            if (_buildValidator is not null && _ctx.WrittenFiles.Count > 0)
            {
                var (success, errorOutput) = await _buildValidator.TryValidateAsync(_ctx.WrittenFiles, ct);
                if (!success)
                {
                    _ctx.Screen.Write("⚠ Build failed, feeding errors back to agent...", ConsoleColor.Yellow);
                    var errorMsg = BuildValidator.CreateBuildErrorMessage(_ctx.Config.BuildCommand!, errorOutput);
                    _ctx.History.Add(errorMsg);
                    Wire.Write("build.validation_failed", "files={0}", string.Join(", ", _ctx.WrittenFiles));
                    // Don't complete subtask - let agent fix the build errors
                    // Check circuit breaker
                    foreach (var file in _ctx.WrittenFiles)
                    {
                        var cbResult = _circuitBreaker.RecordEdit(file, errorOutput);
                        if (cbResult.ShouldStop)
                        {
                            _ctx.Screen.Write($"🚨 {cbResult.Message}", ConsoleColor.Red, EntryStyle.Error);
                            var cbMsg = new ChatMessage
                            {
                                Role = "user",
                                Content = JsonValue.Create(
                                    $"CIRCUIT BREAKER TRIGGERED: {cbResult.Message}\n\n" +
                                    "The agent has attempted to fix the same error multiple times without success. " +
                                    "Please review the file manually or provide new guidance.")
                            };
                            _ctx.History.Add(cbMsg);
                            Wire.Write("circuit_breaker", "file={0} message={1}", cbResult.FilePath, cbResult.Message);
                            return;
                        }
                    }

                    return;
                }
            }

            if (!await TryCompleteCurrentSubtaskAsync(reply.Text.Trim(), traceId, _ctx.History))
            {
                return;
            }

            if (_ctx.Planner is not null && !_ctx.Planner.HasNext)
            {
                await FinishAllSubtasksAsync(traceId);
                return;
            }
        }

        var results = new JsonArray();
        var stop = false;
        var showedAutoThought = false;

        foreach (var original in reply.ToolCalls)
        {
            ct.ThrowIfCancellationRequested();
            var call = original;

            while (true)
            {
                var autoApproved = ToolRisk.Allowed(_ctx.Config.Approvals, call.Name);

                if (!autoApproved)
                {
                    await ShowApprovalPreviewAsync(call);
                }

                var choice = autoApproved ? BarrierChoice.Run : _ctx.Screen.Ask(reply.Text.Trim(), Describe(call));

                Wire.Write("barrier", "{0} → {1}", call.Name, autoApproved ? $"auto ({_ctx.Config.ApprovalMode})" : choice.ToString());

                if (autoApproved && !showedAutoThought)
                {
                    if (reply.Text.Trim().Length > 0)
                    {
                        _ctx.Screen.Write(reply.Text.Trim(), ConsoleColor.DarkCyan, EntryStyle.Thought);
                        _ctx.Screen.Write("");
                    }
                    showedAutoThought = true;
                }

                if (choice == BarrierChoice.Quit)
                {
                    results.Add(CreateErrorResult(call.Id, "The user stopped the session before this action ran."));
                    stop = true;
                    break;
                }

                if (choice == BarrierChoice.Edit)
                {
                    _ctx.Screen.HoldKeyboard();
                    try
                    {
                        call = EditCall(call);
                    }
                    finally
                    {
                        _ctx.Screen.ReleaseKeyboard();
                    }
                    _ctx.Screen.Write(Describe(call), ConsoleColor.White, EntryStyle.Tool);
                    Wire.Write("barrier.edit", "{0} → new arguments", call.Name);
                    Wire.Json("barrier.edit.args", call.Arguments);
                    continue;
                }

                if (autoApproved)
                    _ctx.Screen.Write(Describe(call), ConsoleColor.White, EntryStyle.Tool);

                _ctx.Log.Action(call.Name, call.Arguments);
                Wire.Write("tool", "→ {0} · project={1}", call.Name, _ctx.Project.Root);
                Wire.Json("tool.args", call.Arguments);

                if (call.Name is "write" or "edit" or "question")
                {
                    _ctx.MarkWriteToolsUsed(ReadArg(call.Arguments, "path"));
                    var content = ReadArg(call.Arguments, "content");
                    if (call.Name == "write")
                    {
                        _ctx.Modes.RecordArtifact(_ctx.Modes.Effective, ReadArg(call.Arguments, "path"), content);
                    }
                    else if (call.Name == "edit")
                    {
                        var existing = _ctx.Modes.TryReadArtifact(ReadArg(call.Arguments, "path"), out var current) ? current : "";
                        _ctx.Modes.RecordArtifact(_ctx.Modes.Effective, ReadArg(call.Arguments, "path"), existing);
                    }
                }

                var toolClock = Stopwatch.StartNew();
                var result = await _ctx.Tools.ExecuteAsync(call);
                if (result.IsError) _ctx.Modes.NoteError(call.Name, result.Output);
                ct.ThrowIfCancellationRequested();
                if (result.IsError) Sound.Error(); else Sound.Success();
                Wire.Timing("tool.done", toolClock, "{0} · is_error={1} · {2} chars of output", call.Name, result.IsError, result.Output.Length);
                Wire.Block("tool.output", result.Output);

                _ctx.Screen.Write((result.IsError ? "error · " : "ok · ") + Fold(result.Output), result.IsError ? ConsoleColor.Red : ConsoleColor.DarkGray, EntryStyle.ToolOutput);
                _ctx.Log.Result(result.Output);

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

        _ctx.History.Add(new ChatMessage { Role = "user", Content = results });
        _ctx.SetToolResults(results);

        if (_ctx.Planner is not null && _ctx.Planner.HasNext && _ctx.Config.SelfCheck && _ctx.HasUsedWriteTools)
        {
            if (!await TryCompleteCurrentSubtaskAsync("", traceId, _ctx.History))
            {
                return;
            }

            if (_ctx.Planner is not null && !_ctx.Planner.HasNext)
            {
                await FinishAllSubtasksAsync(traceId);
                return;
            }
        }

        if (stop)
        {
            _ctx.Screen.Write("(paused)", ConsoleColor.DarkGray);
            Wire.TraceId = _ctx.TurnId;
            Wire.Write("turn", "paused at the barrier · history={0} msgs", _ctx.History.Count);
            Wire.Timing("turn.end", _ctx.TurnClock, "finished");
        }
    }

    private async Task<bool> TryCompleteCurrentSubtaskAsync(string assistantText, string traceId, List<ChatMessage> history)
    {
        if (_ctx.Planner?.Current is null) return true;

        // Обе проверки — файлы и браузер — выполняются до завершения подзадачи.
        // Раньше helper вызывался дважды, и второй вызов помечал выполненной уже
        // следующую подзадачу (или падал на null после последней).
        var filesOk = _ctx.VerifyWrittenFiles();
        var browserOk = true;
        if (filesOk)
        {
            var htmlFile = _ctx.WrittenFiles.FirstOrDefault(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) ?? "index.html";
            browserOk = await VerifyGameWorksViaPreviewAsync(htmlFile, traceId);
        }

        var result = SubtaskHelpers.TryCompleteSubtask(
            _ctx.Planner!, _ctx.Screen, traceId, history,
            filesOk && browserOk,
            filesOk ? "ошибки в браузере" : "файлы не созданы",
            filesOk
                ? SubtaskHelpers.BrowserErrorsRetryFactory(_ctx.Planner!)
                : SubtaskHelpers.FilesMissingRetryFactory(_ctx.Planner!),
            "инструменты использованы, файлы проверены, браузер ОК");

        return result != SubtaskCompletionResult.Retry;
    }

    private async Task FinishAllSubtasksAsync(string traceId)
    {
        _ctx.Screen.Write("Все подзадачи выполнены.", ConsoleColor.Green);
        _ctx.SetPlanner(null);
        _ctx.Modes.AdvanceToNextMode();
        _ctx.RequestStop();

        Wire.TraceId = _ctx.TurnId;
        Wire.Write("turn", "done · all subtasks complete (tools path) · {0} steps · {1} msgs in history", _ctx.Step + 1, _ctx.History.Count);
        Wire.Timing("turn.end", _ctx.TurnClock, "finished");
    }

    private async Task FinishTurnAsync(string traceId, string reason)
    {
        Wire.TraceId = _ctx.TurnId;
        Wire.Write("turn", "done · {0} · {1} steps · {2} msgs in history", reason, _ctx.Step + 1, _ctx.History.Count);
        Wire.Timing("turn.end", _ctx.TurnClock, "finished");
    }

    private async Task<bool> VerifyGameWorksViaPreviewAsync(string htmlFileName, string traceId)
    {
        if (_ctx.PreviewServer is null) return true;

        var url = _ctx.PreviewServer.BaseUrl + htmlFileName;
        _ctx.Screen.Write($"верификация через preview: {url}", ConsoleColor.DarkGray);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (_ctx.PreviewServer.JsErrors.Count > 0)
                {
                    _ctx.Screen.Write("верификация не пройдена: JS-ошибки в браузере", ConsoleColor.Red);
                    foreach (var err in _ctx.PreviewServer.JsErrors.Take(5))
                        _ctx.Screen.Write("  " + err, ConsoleColor.DarkGray);
                    return false;
                }

                if (_ctx.PreviewServer.HasLoaded)
                {
                    _ctx.Screen.Write("верификация пройдена: игра загрузилась без JS-ошибок.", ConsoleColor.Green);
                    return true;
                }

                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            if (_ctx.PreviewServer.JsErrors.Count == 0)
            {
                _ctx.Screen.Write("верификация пройдена: таймаут ожидания, JS-ошибок нет.", ConsoleColor.Green);
                return true;
            }
        }

        if (_ctx.PreviewServer.JsErrors.Count > 0)
        {
            _ctx.Screen.Write("верификация не пройдена: JS-ошибки в браузере", ConsoleColor.Red);
            foreach (var err in _ctx.PreviewServer.JsErrors.Take(5))
                _ctx.Screen.Write("  " + err, ConsoleColor.DarkGray);
            return false;
        }

        _ctx.Screen.Write("верификация пройдена: JS-ошибков не обнаружено.", ConsoleColor.Green);
        return true;
    }

    private async Task ShowApprovalPreviewAsync(ToolCall call)
    {
        if (call.Name == "write")
        {
            var content = ReadArg(call.Arguments, "content");
            var path = ReadArg(call.Arguments, "path");
            var diff = Ui.RenderUnifiedDiff("", content, path);
            _ctx.Screen.Write(diff);
        }
        else if (call.Name == "edit")
        {
            var oldString = ReadArg(call.Arguments, "oldString");
            var newString = ReadArg(call.Arguments, "newString");
            var path = ReadArg(call.Arguments, "path");
            var diff = Ui.RenderUnifiedDiff(oldString, newString, path);
            _ctx.Screen.Write(diff);
        }
    }

    private async Task RevealAsync(string text)
    {
        const int chunk = 3;
        if (text.Length <= chunk)
        {
            _ctx.Screen.Write(text, ConsoleColor.White, EntryStyle.Assistant);
            return;
        }

        _ctx.Screen.BeginStream(ConsoleColor.White, EntryStyle.Assistant);
        for (var i = 0; i < text.Length; i += chunk)
        {
            _ctx.Screen.PushStream(text.Substring(i, Math.Min(chunk, text.Length - i)));
            await Task.Delay(8);
        }
        _ctx.Screen.EndStream();
    }

    private static bool IsTransientApiError(Exception ex)
    {
        if (ex is HttpRequestException hre)
        {
            var msg = ex.Message;
            return msg.Contains("503") || msg.Contains("429") || msg.Contains("502") || msg.Contains("504");
        }
        return false;
    }

    // Helper methods (moved from Program.cs)
    private static bool IsPlaceholderToolName(string name)
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

    private static string Describe(ToolCall call)
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

    private static string ReadArg(string arguments, string name)
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

    private static string Fold(string text, int head = 5, int tail = 5)
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

    private static string Trim(string value, int max)
    {
        var single = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length > max ? single[..max] + "..." : single;
    }

    private static TaskPlanner? TryExtractPlan(string text)
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

    private ToolCall EditCall(ToolCall call)
    {
        var argName = PrimaryArg(call.Name);
        var current = ReadArg(call.Arguments, argName);

        _ctx.Screen.Write($"  {argName} [{Trim(current, 200)}]", ConsoleColor.DarkGray);
        var edited = _ctx.Screen.ReadLine($"{argName} > ");
        if (edited.Action == PromptAction.Exit || edited.Text.Length == 0) return call;

        var node = JsonNode.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments) as JsonObject ?? new JsonObject();
        node[argName] = edited.Text;
        return new ToolCall { Id = call.Id, Name = call.Name, Arguments = node.ToJsonString() };
    }

    private static string PrimaryArg(string tool) => tool switch
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

    private void UpdateUsage()
    {
        var tokens = 0;
        foreach (var message in _ctx.History)
            tokens += (message.Content?.ToJsonString().Length ?? 0) / 4;
        _ctx.Screen.SetUsage(_ctx.History.Count, tokens);
    }

    private static JsonObject CreateErrorResult(string toolUseId, string content)
    {
        return new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = toolUseId,
            ["content"] = content,
            ["is_error"] = true,
        };
    }
}
