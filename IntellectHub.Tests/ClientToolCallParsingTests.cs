using IntellectHub.Client;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Разбор tool_calls от OpenAI-совместимых провайдеров.
///
/// Gemini кладёт arguments строкой с JSON внутри, а не объектом. Если её не
/// раскрыть, клиент получает не объект, а строку, и любой вызов падает с
/// "Missing required arguments" — модель исправно передавала path и content.
/// </summary>
public sealed class ClientToolCallParsingTests
{
    private static AssistantReply Parse(string body) => ApiClient.ParseReply(body);

    [Fact]
    public void StringArgumentsAreUnwrappedIntoAnObject()
    {
        const string body = """
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "tool_calls": [
                  {
                    "id": "call_1",
                    "type": "function",
                    "function": {
                      "name": "write",
                      "arguments": "{\"path\":\"index.html\",\"content\":\"<html></html>\"}"
                    }
                  }
                ]
              },
              "finish_reason": "tool_calls"
            }
          ]
        }
        """;

        var call = Assert.Single(Parse(body).ToolCalls);
        Assert.Equal("write", call.Name);
        Assert.Equal("call_1", call.Id);

        using var args = System.Text.Json.JsonDocument.Parse(call.Arguments);
        Assert.Equal("index.html", args.RootElement.GetProperty("path").GetString());
        Assert.Equal("<html></html>", args.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void ObjectArgumentsAreStillAccepted()
    {
        const string body = """
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "tool_calls": [
                  {
                    "id": "call_2",
                    "type": "function",
                    "function": {
                      "name": "read",
                      "arguments": { "path": "a.txt" }
                    }
                  }
                ]
              }
            }
          ]
        }
        """;

        var call = Assert.Single(Parse(body).ToolCalls);
        using var args = System.Text.Json.JsonDocument.Parse(call.Arguments);
        Assert.Equal("a.txt", args.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void AnthropicInputObjectsAreStillAccepted()
    {
        const string body = """
        {
          "content": [
            { "type": "tool_use", "id": "toolu_1", "name": "glob", "input": { "pattern": "**/*" } }
          ]
        }
        """;

        var call = Assert.Single(Parse(body).ToolCalls);
        Assert.Equal("glob", call.Name);
        using var args = System.Text.Json.JsonDocument.Parse(call.Arguments);
        Assert.Equal("**/*", args.RootElement.GetProperty("pattern").GetString());
    }

    /// <summary>
    /// Полный сценарий со скриншота: Gemini вернул write со строковыми arguments,
    /// и он должен дойти до инструмента и создать файл, а не упасть с
    /// "Missing required arguments: path, content".
    /// </summary>
    [Fact]
    public void AGeminiWriteCallSurvivesParsingAndExecutes()
    {
        const string body = """
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "tool_calls": [
                  {
                    "id": "call_1",
                    "type": "function",
                    "function": {
                      "name": "write",
                      "arguments": "{\"path\":\"index.html\",\"content\":\"<html></html>\"}"
                    }
                  }
                ]
              }
            }
          ]
        }
        """;

        var call = Assert.Single(ApiClient.ParseReply(body).ToolCalls);
        var root = Path.Combine(Path.GetTempPath(), "ih-parse-write", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = new ToolRegistry(root)
                .ExecuteAsync(new IntellectHub.Client.ToolCall { Name = call.Name, Arguments = call.Arguments })
                .GetAwaiter().GetResult();

            Assert.False(result.IsError, result.Output);
            Assert.True(File.Exists(Path.Combine(root, "index.html")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
