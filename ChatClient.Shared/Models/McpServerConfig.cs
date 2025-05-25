using System.Text.Json.Serialization;

namespace ChatClient.Shared.Models;

public class McpServerConfig
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;

    // Local process configuration
    public string? Command { get; set; }
    public string[]? Arguments { get; set; }

    // Network configuration
    [JsonPropertyName("sse")]
    public string? Sse { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
