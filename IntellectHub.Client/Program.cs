using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntellectHub.Client;

var config = ClientConfig.Load();

if (args.Length > 0 && args[0] is "--help" or "-h")
{
    Console.WriteLine("IntellectHub Console - a Claude-style terminal client");
    Console.WriteLine("usage: IntellectHub.Client [--url <server>] [--model <name>] [--project <path>]");
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

var project = Project.Open(ResolveProjectRoot());
var tools = new ToolRegistry(project.Root);
var log = new SessionLog(project.LogPath);

RefreshHeader();
screen.Write("IntellectHub Console — type a message, or /help for commands.", ConsoleColor.DarkGray);
screen.Write("");
await CheckHealthAsync();

while (true)
{
    var input = screen.ReadLine();
    if (input is null) break;
    if (input.Length == 0) continue;

    if (input is "/exit" or "/quit") break;
    if (input == "/help") { ShowHelp(); continue; }
    if (input == "/clear") { history.Clear(); screen.Clear(); continue; }
    if (input == "/models") { await ShowModelsAsync(); continue; }
    if (input == "/config") { EditConfig(); continue; }
    if (input == "/project") { SelectProject(); continue; }

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
        screen.Write("error: " + ex.Message, ConsoleColor.Red);
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

void RefreshHeader()
{
    screen.SetHeader(Banner.Header(config, project));
    screen.SetTools(tools.Specs.Select(spec => spec.Name));
}

async Task RunTurnAsync()
{
    for (var step = 0; step < 16; step++)
    {
        screen.Busy(step == 0 ? "thinking" : "working");

        AssistantReply reply;
        try
        {
            reply = await api.SendAsync(history, tools.Specs);
        }
        finally
        {
            screen.Idle();
        }

        if (reply.Text.Trim().Length > 0)
            log.Thought(reply.Text.Trim());

        if (!reply.HasToolCalls)
        {
            if (reply.Text.Trim().Length > 0)
                screen.Write(reply.Text.Trim(), ConsoleColor.White);
            history.Add(new ChatMessage { Role = "assistant", Content = JsonValue.Create(reply.Text) });
            return;
        }

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
        history.Add(new ChatMessage { Role = "assistant", Content = assistantContent });

        var results = new JsonArray();
        var stop = false;

        foreach (var original in reply.ToolCalls)
        {
            var call = original;

            while (true)
            {
                var choice = screen.Ask(reply.Text.Trim(), Describe(call));

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
                    continue;
                }

                log.Action(call.Name, call.Arguments);
                var result = await tools.ExecuteAsync(call);
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
            return;
        }
    }

    screen.Write("(stopped after 16 tool steps)", ConsoleColor.DarkGray);
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

async Task CheckHealthAsync()
{
    try
    {
        await api.HealthAsync();
        screen.Write("connected to " + config.BaseUrl, ConsoleColor.DarkGray);
    }
    catch (Exception ex)
    {
        screen.Write("cannot reach " + config.BaseUrl + ": " + ex.Message, ConsoleColor.Red);
        screen.Write("start the server or pass --url, then retry", ConsoleColor.DarkGray);
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

void SelectProject()
{
    screen.Write("  current [" + project.Root + "]", ConsoleColor.DarkGray);
    var path = screen.ReadLine("project path > ");
    if (path is null or "") return;

    try
    {
        project = Project.Open(path);
        tools = new ToolRegistry(project.Root);
        log = new SessionLog(project.LogPath);
        history.Clear();
        config.Project = project.Root;
        config.Save();
        RefreshHeader();
        screen.Clear();
        RefreshHeader();
    }
    catch (Exception ex)
    {
        screen.Write("error: " + ex.Message, ConsoleColor.Red);
    }
}

void EditConfig()
{
    while (true)
    {
        PrintConfig();
        screen.Write("edit: 1 server  2 model  3 key  4 system  5 maxTokens  6 temperature", ConsoleColor.DarkGray);
        screen.Write("type a number, [s] save, [q] cancel", ConsoleColor.DarkGray);

        var choice = screen.ReadLine("config > ");
        if (choice is null or "q" or "quit" or "cancel") return;
        if (choice is "s" or "save")
        {
            config.Save();
            Reconnect();
            screen.Write("saved to " + ClientConfig.ConfigPath, ConsoleColor.Green);
            return;
        }

        switch (choice)
        {
            case "1":
                var url = Prompt("server", config.BaseUrl);
                if (url is not null) config.BaseUrl = url;
                break;
            case "2":
                var model = Prompt("model", config.Model);
                if (model is not null) config.Model = model;
                break;
            case "3":
                var key = Prompt("api key (empty to clear)", config.ApiKey ?? "");
                if (key is not null) config.ApiKey = string.IsNullOrWhiteSpace(key) ? null : key;
                break;
            case "4":
                var system = Prompt("system", config.System);
                if (system is not null) config.System = system;
                break;
            case "5":
                var max = Prompt("maxTokens", config.MaxTokens.ToString(CultureInfo.InvariantCulture));
                if (max is not null && int.TryParse(max, out var parsedMax) && parsedMax > 0) config.MaxTokens = parsedMax;
                else if (max is not null) screen.Write("invalid number, keeping " + config.MaxTokens, ConsoleColor.Red);
                break;
            case "6":
                var temp = Prompt("temperature", config.Temperature.ToString(CultureInfo.InvariantCulture));
                if (temp is not null && double.TryParse(temp, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedTemp))
                    config.Temperature = parsedTemp;
                else if (temp is not null) screen.Write("invalid number, keeping " + config.Temperature, ConsoleColor.Red);
                break;
            default:
                screen.Write("unknown option: " + choice, ConsoleColor.Red);
                break;
        }
    }
}

string? Prompt(string label, string current)
{
    screen.Write($"  {label} [{Trim(current, 120)}]", ConsoleColor.DarkGray);
    var value = screen.ReadLine($"{label} > ");
    return value is null or "" ? null : value;
}

void Reconnect()
{
    api.Dispose();
    api = new ApiClient(config);
    screen.Clear();
    RefreshHeader();
}

void PrintConfig()
{
    screen.Write("server      : " + config.BaseUrl);
    screen.Write("model       : " + config.Model);
    screen.Write("key         : " + (string.IsNullOrWhiteSpace(config.ApiKey) ? "(none)" : "****" + config.ApiKey[^Math.Min(4, config.ApiKey.Length)..]));
    screen.Write("system      : " + Trim(config.System, 120));
    screen.Write("maxTokens   : " + config.MaxTokens);
    screen.Write("temperature : " + config.Temperature);
    screen.Write("project     : " + project.Root);
    screen.Write("config file : " + ClientConfig.ConfigPath, ConsoleColor.DarkGray);
}

void ShowHelp()
{
    screen.Write("commands", ConsoleColor.White);
    screen.Write("  /help     show this help", ConsoleColor.DarkGray);
    screen.Write("  /models   list models from the server", ConsoleColor.DarkGray);
    screen.Write("  /config   view and edit connection settings", ConsoleColor.DarkGray);
    screen.Write("  /project  switch the working project", ConsoleColor.DarkGray);
    screen.Write("  /clear    clear the conversation", ConsoleColor.DarkGray);
    screen.Write("  /exit     quit", ConsoleColor.DarkGray);
    screen.Write("tools: " + string.Join(", ", tools.Specs.Select(s => s.Name)), ConsoleColor.DarkGray);
    screen.Write("barrier: [Enter] run  [s] step  [e] edit  [q] stop", ConsoleColor.DarkGray);
    screen.Write("keys: Up/Down history | Esc clear input", ConsoleColor.DarkGray);
}
