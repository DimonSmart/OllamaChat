using System.Text.Json.Serialization;

namespace ChatClient.Api.Models;

public class McpServerConfig
{
    public string? Name { get; set; }

    // Local process configuration
    public string? Command { get; set; }
    public string[]? Arguments { get; set; }

    // Network configuration
    [JsonPropertyName("sse")]
    public string? Sse { get; set; }
}
