using System.Text.Json;

namespace ChatClient.Shared.Models;

public record McpToolInfo(string Name, string? Description, JsonElement JsonSchema);
