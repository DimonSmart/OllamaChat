namespace ChatClient.Api.Models;

public class McpServerConfig
{
    public string? Name { get; set; }

    // Local process configuration
    public string? Command { get; set; }
    public string[]? Arguments { get; set; }

    // Network configuration
    public string? Host { get; set; }

    // Connection type
    public McpServerConnectionType ConnectionType { get; set; } = McpServerConnectionType.Local;

    public enum McpServerConnectionType
    {
        Local,
        Network
    }
}
