using System.Text.Json;
using Xunit;

namespace IntellectHub.Tests;

public sealed class ToolPassthroughTests
{
    private static readonly string[] AgentTools =
    {
        "execute_command",
        "view_file",
        "write_file",
        "patch_file",
        "list_dir",
        "search_web",
        "web_fetch",
        "make_http_request",
    };

    private static JsonElement Text(string value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void SystemPrompt_WithAgentToolManifest_KeepsEveryToolName()
    {
        var manifest = BuildManifest();
        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = Text(manifest) },
            new() { Role = "user", Content = Text("run the build") },
        };

        var (prompt, system) = PromptFormatter.Format(messages);

        foreach (var tool in AgentTools)
            Assert.Contains(tool, system);
        Assert.Contains("run the build", prompt);
    }

    [Fact]
    public void AssistantToolCall_RoundTripsThroughPromptForEveryTool()
    {
        foreach (var tool in AgentTools)
        {
            var messages = new List<ChatMessage>
            {
                new()
                {
                    Role = "assistant",
                    ToolCalls = new List<ToolCall>
                    {
                        new()
                        {
                            Id = "call_1",
                            Type = "function",
                            Function = new ToolCallFunction { Name = tool, Arguments = "{}" },
                        },
                    },
                },
            };

            var (prompt, _) = PromptFormatter.Format(messages);

            Assert.Contains("TOOL_CALL: " + tool, prompt);
        }
    }

    [Fact]
    public void ToolResult_IsFormattedBackToTheModel()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = "tool", ToolCallId = "call_1", Content = Text("exit code 0") },
        };

        var (prompt, _) = PromptFormatter.Format(messages);

        Assert.Contains("[Tool Result]", prompt);
        Assert.Contains("exit code 0", prompt);
    }

    private static string BuildManifest()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You have access to these tools:");
        foreach (var tool in AgentTools)
            sb.AppendLine("- " + tool);
        return sb.ToString();
    }
}
