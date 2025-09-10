namespace ChatClient.Domain.Models;

/// <summary>
/// Represents a call to a function on an MCP server.
/// </summary>
/// <param name="Server">The server name.</param>
/// <param name="Function">The function name.</param>
/// <param name="Request">The request parameters as text.</param>
/// <param name="Response">The response from the server.</param>
public record FunctionCallRecord(string Server, string Function, string Request, string Response);
