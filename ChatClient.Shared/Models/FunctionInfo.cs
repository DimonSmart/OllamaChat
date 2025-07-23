namespace ChatClient.Shared.Models;

public class FunctionInfo
{
    /// <summary>
    /// Unique name of the function used internally. Formatted as
    /// "ServerName:FunctionName" to avoid collisions between servers.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The MCP server providing this function.
    /// </summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>
    /// Name of the function as provided by the server.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
}
