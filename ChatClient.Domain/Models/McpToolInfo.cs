using System.Text.Json;

namespace ChatClient.Domain.Models;

public record McpToolInfo(string Name, string? Description, JsonElement JsonSchema);
