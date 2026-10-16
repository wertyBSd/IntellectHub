using System.Text.Json.Nodes;
using IntellectHub.Client;

var config = ClientConfig.Load();

if (args.Length > 0 && args[0] is "--help" or "-h")
{
    Console.WriteLine("IntellectHub Console - a Claude-style terminal client");
    Console.WriteLine("usage: IntellectHub.Client [--url <server>] [--model <name>]");
    Console.WriteLine($"config: {ClientConfig.ConfigPath}");
    return;
}

for (var i = 0; i + 1 < args.Length; i++)
{
    switch (args[i])
    {
        case "--url" or "-u": config.BaseUrl = args[i + 1]; break;
        case "--model" or "-m": config.Model = args[i + 1]; break;
    }
}

using var screen = new Screen();
using var api = new ApiClient(config);
var tools = new ToolRegistry(Directory.GetCurrentDirectory());
var history = new List<ChatMessage>();

Banner.Print(screen, config);
await CheckHealthAsync();

while (true)
{
    var input = screen.ReadLine();
    if (input is null) break;
    if (input.Length == 0) continue;

    if (input is "/exit" or "/quit") break;
    if (input == "/help") { ShowHelp(); continue; }
    if (input == "/clear") { history.Clear(); screen.Clear(); Banner.Print(screen, config); continue; }
    if (input == "/models") { await ShowModelsAsync(); continue; }
    if (input == "/config") { ShowConfig(); continue; }

    screen.Write("you > " + input, ConsoleColor.Cyan);
    screen.Write("");
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

screen.Write("bye", ConsoleColor.DarkGray);

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

        if (!string.IsNullOrWhiteSpace(reply.Text))
            screen.Write(reply.Text.Trim(), ConsoleColor.White);

        if (!reply.HasToolCalls)
        {
            history.Add(new ChatMessage { Role = "assistant", Content = JsonValue.Create(reply.Text) });
            return;
        }

        var assistantContent = new JsonArray();
        if (!string.IsNullOrWhiteSpace(reply.Text))
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
        foreach (var call in reply.ToolCalls)
        {
            screen.Write($"  {call.Name}  {Compact(call.Arguments)}", ConsoleColor.Yellow);
            var result = await tools.ExecuteAsync(call);
            screen.Write(
                $"  {(result.IsError ? "x" : "ok")} {Compact(result.Output)}",
                result.IsError ? ConsoleColor.Red : ConsoleColor.DarkGray);

            results.Add(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = call.Id,
                ["content"] = result.Output,
                ["is_error"] = result.IsError,
            });
        }
        history.Add(new ChatMessage { Role = "user", Content = results });
    }

    screen.Write("(stopped after 16 tool steps)", ConsoleColor.DarkGray);
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

void ShowConfig()
{
    screen.Write("server : " + config.BaseUrl);
    screen.Write("model  : " + config.Model);
    screen.Write("config : " + ClientConfig.ConfigPath);
}

void ShowHelp()
{
    screen.Write("commands", ConsoleColor.White);
    screen.Write("  /help     show this help", ConsoleColor.DarkGray);
    screen.Write("  /models   list models from the server", ConsoleColor.DarkGray);
    screen.Write("  /config   show current connection settings", ConsoleColor.DarkGray);
    screen.Write("  /clear    clear the conversation", ConsoleColor.DarkGray);
    screen.Write("  /exit     quit", ConsoleColor.DarkGray);
    screen.Write("keys: Up/Down history | PageUp/PageDown scroll | Esc clear input", ConsoleColor.DarkGray);
}

static string Compact(string text)
{
    var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
    return single.Length > 160 ? single[..160] + "..." : single;
}
