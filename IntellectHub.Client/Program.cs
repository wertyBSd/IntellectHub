using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntellectHub.Client;

var config = ClientConfig.Load();

if (args.Length > 0 && args[0] is "--help" or "-h")
{
    Console.WriteLine("IntellectHub Console - a Claude-style terminal client");
    Console.WriteLine("usage: IntellectHub.Client [--url <server>] [--model <name>] [--project <path>] [--yes|--approve ask|writes|all]");
    Console.WriteLine($"config: {ClientConfig.ConfigPath}");
    return;
}

for (var i = 0; i + 1 < args.Length; i++)
{
    switch (args[i])
    {
        case "--url" or "-u": config.BaseUrl = args[i + 1]; break;
        case "--model" or "-m": config.Model = args[i + 1]; break;
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
                config.BaseUrl = named.Url;
                config.ServerName = named.Name;
            }
            else
            {
                config.BaseUrl = args[i + 1];
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
    var registry = new ToolRegistry(ResolveProjectRoot());
    var outcome = await registry.ExecuteAsync(new ToolCall { Name = toolName, Arguments = toolArgs });
    Console.WriteLine(outcome.Output);
    Environment.ExitCode = outcome.IsError ? 1 : 0;
    return;
}

using var screen = new Ui();
var api = new ApiClient(config);
var history = new List<ChatMessage>();

// Identity of this conversation for the gateway.
var sessionKey = NewSessionKey();

var project = Project.Open(ResolveProjectRoot());
var tools = new ToolRegistry(project.Root);
var log = new SessionLog(project.LogPath);

// Планировщик подзадач: когда модель возвращает JSON-план со списком подзадач,
// клиент выполняет их по очереди, один за другим.
TaskPlanner? _planner = null;

RefreshHeader();
screen.Write("IntellectHub Console — type a message, or /help for commands.", ConsoleColor.DarkGray);
screen.Write("");
await CheckHealthAsync();

while (true)
{
    var input = screen.ReadLine();
    if (input is null) break;
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

    screen.Write("you > " + input, ConsoleColor.Cyan);
    screen.Write("");
    log.User(input);
    history.Add(new ChatMessage { Role = "user", Content = JsonValue.Create(input) });

    try
    {
        await RunTurnAsync();
    }
    catch (Exception ex)
    {
        Sound.Error();
        screen.Write("error: " + ex.Message, ConsoleColor.Red);
        Wire.Write("turn.err", "{0}: {1}", ex.GetType().Name, ex.Message);
        Wire.Block("turn.err.st", ex.ToString());
    }

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
    Wire.TraceId = null;
    Wire.Write("session.reset", "transcript cleared locally · new gateway session key {0}", sessionKey);
}

void RefreshHeader()
{
    screen.SetHeader(Banner.Header(config, project));
    screen.SetTools(tools.Specs.Select(spec => spec.Name));
}

async Task RunTurnAsync()
{
    var turnClock = System.Diagnostics.Stopwatch.StartNew();
    var turnId = Wire.NewTraceId();

    Wire.TraceId = null;
    Wire.Write("turn", "start · history={0} msgs · tools={1} · planner={2}",
        history.Count, tools.Specs.Count, _planner is not null ? "active" : "none");

    for (var step = 0; step < 16; step++)
    {
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
            var subtaskPrompt = new ChatMessage
            {
                Role = "user",
                Content = JsonValue.Create(
                    $"Выполни подзадачу {subtask.Index + 1} из {_planner.Total}: {subtask.Title}\n\n" +
                    $"Описание: {subtask.Description}\n\n" +
                    $"Используй инструменты write/edit, чтобы записать свою часть результата в файл docs/manual.md. " +
                    $"Не пытайся сделать всё за один вызов — пиши только эту часть. " +
                    $"Когда закончишь, напиши краткий отчёт текстом без вызовов инструментов.")
            };
            history.Add(subtaskPrompt);
            Wire.Write("planner.subtask", "executing subtask {0}/{1}: {2}", subtask.Index + 1, _planner.Total, subtask.Title);
        }

        AssistantReply reply;
        var callClock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            reply = await api.SendAsync(history, tools.Specs, traceId, sessionKey);
            Sound.Notify();
        }
        catch (Exception ex)
        {
            Console.Beep(1047, 60);
            Console.Beep(1318, 80);
            Wire.Write("step.err", "{0}: {1}", ex.GetType().Name, ex.Message);
            throw;
        }
        finally
        {
            screen.Idle();
        }

        Wire.Timing("step", callClock, "round trip finished · stop_reason={0}", reply.StopReason);

        if (reply.Text.Trim().Length > 0)
            log.Thought(reply.Text.Trim());

        // Если все вызовы инструментов — placeholder-имена (noop, no_tool_available и т.д.),
        // трактуем это как отсутствие реальных вызовов: модель отказалась от инструментов.
        var allPlaceholder = reply.ToolCalls.All(c => IsPlaceholderToolName(c.Name));

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
                screen.Write("План принят. Подзадач: " + _planner.Total, ConsoleColor.Yellow);
                screen.Write(_planner.PlanSummary(), ConsoleColor.DarkGray);

                var assistantContent = new JsonArray();
                if (reply.Text.Trim().Length > 0)
                    assistantContent.Add(new JsonObject { ["type"] = "text", ["text"] = reply.Text });
                history.Add(new ChatMessage { Role = "assistant", Content = assistantContent });

                Wire.TraceId = turnId;
                Wire.Write("turn", "plan loaded · {0} subtasks · continuing with subtask execution", _planner.Total);
                continue; // next loop iteration will pick up the first subtask
            }
        }

        if (!reply.HasToolCalls || allPlaceholder)
        {
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
                // Модель закончила текущую подзадачу (текст без вызовов инструментов).
                _planner.MarkCurrentDone(reply.Text.Trim());
                screen.Write($"Подзадача {_planner.Current?.Index ?? _planner.Total} готова.", ConsoleColor.Green);

                var assistantContent = new JsonArray();
                if (reply.Text.Trim().Length > 0)
                    assistantContent.Add(new JsonObject { ["type"] = "text", ["text"] = reply.Text });
                history.Add(new ChatMessage { Role = "assistant", Content = assistantContent });

                if (_planner.HasNext)
                {
                    Wire.TraceId = turnId;
                    Wire.Write("turn", "subtask done · continuing with subtask {0}/{1}",
                        _planner.Current!.Index + 1, _planner.Total);
                    continue; // execute next subtask
                }

                screen.Write("Все подзадачи выполнены.", ConsoleColor.Green);
                _planner = null;

                // Back to the turn id: the step loop overwrote it.
                Wire.TraceId = turnId;
                Wire.Write("turn", "done · all subtasks complete · {0} steps · {1} msgs in history", step + 1, history.Count);
                Wire.Timing("turn.end", turnClock, "finished");
                return;
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

        var results = new JsonArray();
        var stop = false;
        var showedAutoThought = false;

        foreach (var original in reply.ToolCalls)
        {
            var call = original;

            while (true)
            {
                // The approval mode decides whether this call stops for a person at
                // all. Skipping the barrier entirely — rather than answering it on
                // the user's behalf — is what makes unattended runs actually run.
                var autoApproved = ToolRisk.Allowed(config.Approvals, call.Name);
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
                        screen.Write("thought", ConsoleColor.DarkGray);
                        screen.Write("  " + reply.Text.Trim(), ConsoleColor.DarkCyan);
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
                    call = EditCall(call);
                    Wire.Write("barrier.edit", "{0} → new arguments", call.Name);
                    Wire.Json("barrier.edit.args", call.Arguments);
                    continue;
                }

                log.Action(call.Name, call.Arguments);
                Wire.Write("tool", "→ {0} · project={1}", call.Name, project.Root);
                Wire.Json("tool.args", call.Arguments);

                var toolClock = System.Diagnostics.Stopwatch.StartNew();
                var result = await tools.ExecuteAsync(call);
                if (result.IsError) Sound.Error(); else Sound.Success();
                Wire.Timing("tool.done", toolClock, "{0} · is_error={1} · {2} chars of output",
                    call.Name, result.IsError, result.Output.Length);
                Wire.Block("tool.output", result.Output);

                log.Result(result.Output);

                screen.Write($"  {(result.IsError ? "x" : "ok")} {call.Name}",
                    result.IsError ? ConsoleColor.Red : ConsoleColor.DarkGray);
                screen.Write(Fold(result.Output), ConsoleColor.DarkGray);

                results.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = call.Id,
                    ["content"] = result.Output,
                    ["is_error"] = result.IsError,
                });

                if (choice == BarrierChoice.Step) stop = true;
                break;
            }

            if (stop) break;
        }

        history.Add(new ChatMessage { Role = "user", Content = results });

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
    if (edited is null or "") return call;

    var node = JsonNode.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments) as JsonObject ?? new JsonObject();
    node[argName] = edited;
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
        screen.Write(text, ConsoleColor.White);
        return;
    }

    screen.BeginStream(ConsoleColor.White);
    for (var i = 0; i < text.Length; i += chunk)
    {
        screen.PushStream(text.Substring(i, Math.Min(chunk, text.Length - i)));
        await Task.Delay(8);
    }
    screen.EndStream();
}

async Task CheckHealthAsync()
{
    try
    {
        await api.HealthAsync();
        screen.Write("connected to " + config.BaseUrl, ConsoleColor.DarkGray);
        Wire.TraceId = null;
        Wire.Write("health", "gateway reachable at {0}", config.BaseUrl);
    }
    catch (HttpRequestException ex)
    {
        // "No connection could be made" on its own leaves the user with nothing to
        // act on. Name the address, say what it means, and give the command that
        // fixes it.
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

async Task ShowModelsAsync()
{
    try
    {
        var models = await api.ListModelsAsync();
        screen.Write(models.Count == 0 ? "(no models)" : string.Join("\n", models));
    }
    catch (Exception ex)
    {
        screen.Write("error: " + ex.Message, ConsoleColor.Red);
    }
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
    /// Switches gateway. Takes a saved server by name, or a bare URL to use one
    /// that was never saved — both land in the same place, and a named switch also
    /// updates the stored entry so the address stays in step with the name.
    /// </summary>
    async Task ConnectAsync(string arguments)
    {
        var (verb, rest) = Split(arguments);

        if (verb.Length == 0)
        {
            ShowServers();
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

        var known = config.Servers.FirstOrDefault(s =>
            string.Equals(s.Name, verb, StringComparison.OrdinalIgnoreCase));

        if (known is not null)
        {
            config.BaseUrl = known.Url;
            config.ServerName = known.Name;
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
                screen.Write("known servers: " + KnownServerList(), ConsoleColor.DarkGray);
                screen.Write("save one with /connect save <name> <url>", ConsoleColor.DarkGray);
                return;
            }

            setting.Write(config, verb);
            config.ServerName = "";
            config.Save();
            AfterServerChange($"custom → {config.BaseUrl}");
        }

        await CheckHealthAsync();
    }

    void AfterServerChange(string shown)
    {
        Wire.TraceId = null;
        Wire.Write("server", "{0}", shown);
        Reconnect();
        screen.Write($"server = {shown}", ConsoleColor.Green);
    }

    void ShowServers()
    {
        screen.Write("servers", ConsoleColor.White);

        if (config.Servers.Count == 0)
            screen.Write("  (none saved yet — /connect save <name> <url>)", ConsoleColor.DarkGray);
        else
        {
            foreach (var server in config.Servers)
            {
                var active = string.Equals(server.Name, config.ServerName, StringComparison.OrdinalIgnoreCase);
                screen.Write($"  {(active ? "*" : " ")} {server.Name,-14} {server.Url}",
                    active ? ConsoleColor.Green : ConsoleColor.DarkGray);
            }
        }

        screen.Write("");
        screen.Write($"  current: {config.ServerLabel} → {config.BaseUrl}", ConsoleColor.Cyan);
        screen.Write("  /connect <name|url>   switch", ConsoleColor.DarkGray);
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

/// <summary>
/// Changes how much of the agent's work runs unattended. Kept as its own command
/// rather than buried in /config because it is the one setting a person flips
/// mid-conversation: trust the task, run it, then turn it back off.
/// </summary>
void SetApprovals(string arguments)
{
    if (arguments.Length == 0)
    {
        screen.Write($"  approvals  {config.Approvals}", ConsoleColor.Cyan);
        screen.Write($"  {ToolRisk.Describe(config.Approvals)}", ConsoleColor.DarkGray);
        screen.Write("  change it with /approve ask, /approve writes or /approve all", ConsoleColor.DarkGray);
        screen.Write($"  tools: {string.Join(", ", tools.Specs.Select(s => s.Name))}", ConsoleColor.DarkGray);
        screen.Write("  always asked in 'writes': " +
            string.Join(", ", tools.Specs.Select(s => s.Name).Where(ToolRisk.CanWrite)), ConsoleColor.DarkGray);
        return;
    }

    // An unrecognised word must not land on Ask: silently tightening the barrier
    // is the worst possible reading of "yes".
    if (!Enum.TryParse<Approvals>(arguments.Trim(), true, out _) && !IsAffirmative(arguments))
    {
        screen.Write($"unknown approval mode '{arguments}'", ConsoleColor.Red);
        screen.Write("use ask, writes or all", ConsoleColor.DarkGray);
        return;
    }

    config.ApprovalMode = ToolRisk.Normalize(arguments);
    config.Save();
    RefreshHeader();

    Wire.TraceId = null;
    Wire.Write("approvals", "{0} · {1}", config.Approvals, ToolRisk.Describe(config.Approvals));

    screen.Write($"approvals = {config.Approvals}", ConsoleColor.Green);
    screen.Write($"  {ToolRisk.Describe(config.Approvals)}", ConsoleColor.DarkGray);
}

static bool IsAffirmative(string value) =>
    value.Trim() is "y" or "yes" or "on" or "auto" or "all" or "w" or "writes" or "write";

async Task SelectProjectAsync(string arguments)
{
    if (arguments.Length == 0)
    {
        screen.Write($"  current [{project.Root}]", ConsoleColor.DarkGray);
        screen.Write("  switch it with /project <path>", ConsoleColor.DarkGray);
        return;
    }

    await Task.Yield();

    try
    {
        project = Project.Open(arguments);
        tools = new ToolRegistry(project.Root);
        log = new SessionLog(project.LogPath);
        // A new workspace means a new conversation, so the remote chat behind this
        // one has to be released along with the transcript.
        history.Clear();
        sessionKey = NewSessionKey();
        config.Project = project.Root;
        config.Save();
        RefreshHeader();
        screen.Clear();
    }
    catch (Exception ex)
    {
        screen.Write("error: " + ex.Message, ConsoleColor.Red);
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

        value = screen.ReadLine($"{setting.Name} > ");
        if (value is null or "") return;
    }

    await ApplySettingAsync(setting, value);
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
    Wire.TraceId = null;
    Wire.Write("config", "{0} = {1}", setting.Name, shown);
    screen.Write($"{setting.Name} = {Trim(shown, 160)}", ConsoleColor.Green);
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
        Reconnect();
        screen.Write("settings restored to defaults (key left as it was)", ConsoleColor.Green);
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
    api = new ApiClient(config);
    RefreshHeader();
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

    var userText = lastUserMsg.Content.ToString();
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
        await RunTurnAsync();
    }
    catch (Exception ex)
    {
        Sound.Error();
        screen.Write("error: " + ex.Message, ConsoleColor.Red);
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
    screen.Write("settings: " + string.Join(", ", Setting.All.Select(s => s.Name)), ConsoleColor.DarkGray);
    screen.Write("tools: " + string.Join(", ", tools.Specs.Select(s => s.Name)), ConsoleColor.DarkGray);
    screen.Write("barrier: [Enter] run  [s] step  [e] edit  [q] stop", ConsoleColor.DarkGray);
    screen.Write("keys: Up/Down history | Esc clear input", ConsoleColor.DarkGray);
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

static class Sound
{
    private static readonly bool _isWindows = OperatingSystem.IsWindows();

    public static void Notify() => Play("Asterisk");
    public static void Success() => Play("Beep");
    public static void Error() => Play("Hand");

    private static void Play(string sound)
    {
        if (!_isWindows) { Console.Beep(); return; }
        try
        {
            var asm = Assembly.Load("System.Media");
            var type = asm.GetType("System.Media.SystemSounds")!;
            var prop = type.GetProperty(sound, BindingFlags.Public | BindingFlags.Static)!;
            var soundObj = prop.GetValue(null)!;
            var playMethod = soundObj.GetType().GetMethod("Play", BindingFlags.Public | BindingFlags.Instance)!;
            playMethod.Invoke(soundObj, null);
        }
        catch { Console.Beep(); }
    }
}
    
