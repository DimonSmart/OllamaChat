using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/mcp-playground")]
public class McpPlaygroundController(IMcpClientService clientService) : ControllerBase
{
    [HttpGet("servers")]
    public async Task<ActionResult<IEnumerable<string>>> GetServers(CancellationToken cancellationToken)
    {
        var clients = await clientService.GetMcpClientsAsync(cancellationToken);
        return Ok(clients.Select(c => c.ServerInfo.Name));
    }

    [HttpGet("tools/{server}")]
    public async Task<ActionResult<IEnumerable<McpToolInfo>>> GetTools(string server, CancellationToken cancellationToken)
    {
        var clients = await clientService.GetMcpClientsAsync(cancellationToken);
        var client = clients.FirstOrDefault(c => string.Equals(c.ServerInfo.Name, server, StringComparison.OrdinalIgnoreCase));
        if (client == null)
            return NotFound();
        var tools = await clientService.GetMcpTools(client, cancellationToken);
        var result = tools.Select(t => new McpToolInfo(t.Name, t.Description, t.JsonSchema));
        return Ok(result);
    }

    [HttpPost("call")]
    public async Task<ActionResult<JsonElement>> Call(McpFunctionCallRequest request, CancellationToken cancellationToken)
    {
        var clients = await clientService.GetMcpClientsAsync(cancellationToken);
        var client = clients.FirstOrDefault(c => string.Equals(c.ServerInfo.Name, request.Server, StringComparison.OrdinalIgnoreCase));
        if (client == null)
            return NotFound($"Server {request.Server} not found");
        var tool = (await clientService.GetMcpTools(client, cancellationToken))
            .FirstOrDefault(t => string.Equals(t.Name, request.Function, StringComparison.OrdinalIgnoreCase));
        if (tool == null)
            return NotFound($"Function {request.Function} not found");
        var args = request.Parameters?.ToDictionary(kv => kv.Key, kv => kv.Value.Deserialize<object?>()) ?? new Dictionary<string, object?>();
        var result = await tool.CallAsync(args, null, null, cancellationToken);
        return Ok(JsonSerializer.SerializeToElement(result));
    }
}
