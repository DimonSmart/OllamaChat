using System.Text.Json.Serialization;

namespace ChatClient.Shared.Models;

public class McpServerConfig
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public string? Command { get; set; }
    public string[]? Arguments { get; set; }

    [JsonPropertyName("sse")]
    public string? Sse { get; set; }

    public string? SamplingModel { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
