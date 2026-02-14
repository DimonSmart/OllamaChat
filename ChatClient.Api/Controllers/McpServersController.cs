using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class McpServersController(IMcpServerConfigService mcpServerConfigService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<IMcpServerDescriptor>>> GetAllServers()
    {
        var servers = await mcpServerConfigService.GetAllAsync();
        return Ok(servers);
    }

    [HttpGet("{serverId}")]
    public async Task<ActionResult<IMcpServerDescriptor>> GetServerById(Guid serverId)
    {
        var server = await mcpServerConfigService.GetByIdAsync(serverId);
        if (server == null)
        {
            return NotFound($"MCP server config with ID {serverId} not found");
        }

        return Ok(server);
    }

    [HttpPost]
    public async Task<ActionResult<McpServerConfig>> CreateServer([FromBody] McpServerConfig server)
    {
        if (server.IsBuiltIn)
        {
            return BadRequest("Built-in MCP servers are managed by the application.");
        }

        if (string.IsNullOrWhiteSpace(server.Name))
        {
            return BadRequest("Server name is required");
        }

        if (string.IsNullOrWhiteSpace(server.Command) && string.IsNullOrWhiteSpace(server.Sse))
        {
            return BadRequest("Either Command or Sse must be specified");
        }

        await mcpServerConfigService.CreateAsync(server);
        return CreatedAtAction(nameof(GetServerById), new { serverId = server.Id }, server);
    }

    [HttpPut("{serverId}")]
    public async Task<ActionResult<McpServerConfig>> UpdateServer(Guid serverId, [FromBody] McpServerConfig server)
    {
        if (serverId != server.Id)
        {
            return BadRequest("ID in URL does not match ID in request body");
        }

        if (server.IsBuiltIn)
        {
            return BadRequest("Built-in MCP servers are managed by the application.");
        }

        if (string.IsNullOrWhiteSpace(server.Name))
        {
            return BadRequest("Server name is required");
        }

        if (string.IsNullOrWhiteSpace(server.Command) &&
            string.IsNullOrWhiteSpace(server.Sse))
        {
            return BadRequest("Either Command or Sse must be specified");
        }

        var existingServer = await mcpServerConfigService.GetByIdAsync(serverId);
        if (existingServer == null)
        {
            return NotFound($"MCP server config with ID {serverId} not found");
        }

        if (existingServer is not McpServerConfig)
        {
            return BadRequest("Built-in MCP servers are managed by the application.");
        }

        await mcpServerConfigService.UpdateAsync(server);
        return Ok(server);
    }

    [HttpDelete("{serverId}")]
    public async Task<ActionResult> DeleteServer(Guid serverId)
    {
        var existingServer = await mcpServerConfigService.GetByIdAsync(serverId);
        if (existingServer == null)
        {
            return NotFound($"MCP server config with ID {serverId} not found");
        }

        if (existingServer is not McpServerConfig)
        {
            return BadRequest("Built-in MCP servers are managed by the application.");
        }

        await mcpServerConfigService.DeleteAsync(serverId);
        return NoContent();
    }
}
