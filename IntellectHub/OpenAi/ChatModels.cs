using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntellectHub;

internal sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("messages")] public List<ChatMessage>? Messages { get; set; }
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    [JsonPropertyName("user")] public string? User { get; set; }
    [JsonPropertyName("tools")] public List<ToolDefinition>? Tools { get; set; }

    /// <summary>Stable identity for one conversation, taken from the x-agent-session header.</summary>
    [JsonIgnore] public string? SessionKey { get; set; }
}

internal sealed class ChatMessage
{
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("content")] public JsonElement Content { get; set; }
    [JsonPropertyName("tool_calls")] public List<ToolCall>? ToolCalls { get; set; }
    [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }
}

internal sealed class ToolCall
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("function")] public ToolCallFunction? Function { get; set; }
}

internal sealed class ToolCallFunction
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("arguments")] public string? Arguments { get; set; }
}

internal sealed class ToolDefinition
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("function")] public ToolDefinitionFunction? Function { get; set; }

    /// <summary>Flat name, for clients that send <c>{"name":...}</c> instead of a function object.</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }
}

internal sealed class ToolDefinitionFunction
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }

    [JsonPropertyName("parameters")] public JsonElement? Parameters { get; set; }

    /// <summary>The schema as sent by the client, kept verbatim for the prompt manifest.</summary>
    [JsonIgnore]
    public string? ParametersRaw =>
        Parameters is { ValueKind: JsonValueKind.Object } value ? value.GetRawText() : null;
}