namespace ChatClient.Shared.Models;

/// <summary>
/// Ответ MCP‑серверу по JSON‑RPC 2.0.
/// </summary>
public record JsonRpcResponse(
    int     Id,
    object? Result,
    object? Error = null
);
