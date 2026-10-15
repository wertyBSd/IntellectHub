using System.Net.Http.Json;
using System.Text.Json;

var baseUrl = args.Length > 0 ? args[0] : "http://127.0.0.1:9656";
var prompt = args.Length > 1 ? args[1] : "Say hello in one short sentence.";

using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };

Console.WriteLine($"IntellectHub test client -> {baseUrl}");
Console.WriteLine(new string('-', 60));

await CheckHealthAsync();
await CheckModelsAsync();
await CheckChatCompletionsAsync();
await CheckAnthropicMessagesAsync();
await CheckResponsesAsync();

async Task CheckHealthAsync()
{
    Console.WriteLine("[GET /health]");
    try
    {
        var text = await http.GetStringAsync("/health");
        Console.WriteLine("  OK  " + Trim(text));
    }
    catch (Exception ex)
    {
        Console.WriteLine("  FAIL " + ex.Message);
    }
    Console.WriteLine();
}

async Task CheckModelsAsync()
{
    Console.WriteLine("[GET /v1/models]");
    try
    {
        var text = await http.GetStringAsync("/v1/models");
        Console.WriteLine("  OK  " + Trim(text));
    }
    catch (Exception ex)
    {
        Console.WriteLine("  FAIL " + ex.Message);
    }
    Console.WriteLine();
}

async Task CheckChatCompletionsAsync()
{
    Console.WriteLine("[POST /v1/chat/completions]");
    var body = new
    {
        model = "deepseek-v4-flash",
        messages = new[] { new { role = "user", content = prompt } },
    };
    await PostAsync("/v1/chat/completions", body, text =>
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? "";
    });
}

async Task CheckAnthropicMessagesAsync()
{
    Console.WriteLine("[POST /v1/messages]");
    var body = new
    {
        model = "deepseek-v4-flash",
        max_tokens = 1024,
        messages = new[] { new { role = "user", content = prompt } },
    };
    await PostAsync("/v1/messages", body, text =>
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    });
}

async Task CheckResponsesAsync()
{
    Console.WriteLine("[POST /v1/responses]");
    var body = new
    {
        model = "deepseek-v4-flash",
        input = prompt,
    };
    await PostAsync("/v1/responses", body, text =>
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("output")[0]
            .GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    });
}

async Task PostAsync(string path, object body, Func<string, string> extract)
{
    try
    {
        using var response = await http.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"  FAIL HTTP {(int)response.StatusCode}: {Trim(text)}");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("  OK  " + Trim(extract(text)));
    }
    catch (Exception ex)
    {
        Console.WriteLine("  FAIL " + ex.Message);
    }
    Console.WriteLine();
}

static string Trim(string value)
{
    var single = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
    return single.Length > 300 ? single[..300] + "..." : single;
}
