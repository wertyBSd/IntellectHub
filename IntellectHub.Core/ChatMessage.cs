using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace IntellectHub.Client;

public sealed class ChatMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public JsonNode? Content { get; set; }
}
