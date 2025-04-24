using System.Text.Json;

namespace ChatClient.Shared.Models;

/// <summary>
/// Формат JSON‑RPC 2.0 при обращении к MCP‑серверу.
/// </summary>
public record JsonRpcRequest(
    string      Jsonrpc,
    int         Id,
    string      Method,
    JsonElement Params
);

