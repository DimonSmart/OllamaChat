namespace ChatClient.Shared.Models;

/// <summary>
/// Describes a function exposed by an MCP server.
/// The <c>Name</c> property uses format "ServerName:FunctionName" to avoid collisions.
/// </summary>
public class FunctionInfo
{
    public string Name { get; set; } = string.Empty;

    public string ServerName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
}
