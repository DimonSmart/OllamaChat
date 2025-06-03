using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using Microsoft.AspNetCore.Mvc;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class McpServersController(IMcpServerConfigService mcpServerConfigService, ILogger<McpServersController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<McpServerConfig>>> GetAllServers()
    {
        try
        {
            var servers = await mcpServerConfigService.GetAllServersAsync();
            return Ok(servers);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving all MCP server configs");
            return StatusCode(500, "An error occurred while retrieving MCP server configs");
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<McpServerConfig>> GetServerById(Guid id)
    {
        try
        {
            var server = await mcpServerConfigService.GetServerByIdAsync(id);
            if (server == null)
            {
                return NotFound($"MCP server config with ID {id} not found");
            }

            return Ok(server);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving MCP server config with ID {Id}", id);
            return StatusCode(500, "An error occurred while retrieving the MCP server config");
        }
    }

    [HttpPost]
    public async Task<ActionResult<McpServerConfig>> CreateServer([FromBody] McpServerConfig server)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(server.Name))
            {
                return BadRequest("Server name is required");
            }

            if (string.IsNullOrWhiteSpace(server.Command) && string.IsNullOrWhiteSpace(server.Sse))
            {
                return BadRequest("Either Command or Sse must be specified");
            }

            var createdServer = await mcpServerConfigService.CreateServerAsync(server);
            return CreatedAtAction(nameof(GetServerById), new { id = createdServer.Id }, createdServer);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating MCP server config");
            return StatusCode(500, "An error occurred while creating the MCP server config");
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<McpServerConfig>> UpdateServer(Guid id, [FromBody] McpServerConfig server)
    {
        try
        {
            if (id != server.Id)
            {
                return BadRequest("ID in URL does not match ID in request body");
            }

            if (string.IsNullOrWhiteSpace(server.Name))
            {
                return BadRequest("Server name is required");
            }

            if (string.IsNullOrWhiteSpace(server.Command) && string.IsNullOrWhiteSpace(server.Sse))
            {
                return BadRequest("Either Command or Sse must be specified");
            }

            var existingServer = await mcpServerConfigService.GetServerByIdAsync(id);
            if (existingServer == null)
            {
                return NotFound($"MCP server config with ID {id} not found");
            }

            var updatedServer = await mcpServerConfigService.UpdateServerAsync(server);
            return Ok(updatedServer);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating MCP server config with ID {Id}", id);
            return StatusCode(500, "An error occurred while updating the MCP server config");
        }
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteServer(Guid id)
    {
        try
        {
            var existingServer = await mcpServerConfigService.GetServerByIdAsync(id);
            if (existingServer == null)
            {
                return NotFound($"MCP server config with ID {id} not found");
            }

            await mcpServerConfigService.DeleteServerAsync(id);
            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting MCP server config with ID {Id}", id);
            return StatusCode(500, "An error occurred while deleting the MCP server config");
        }
    }
}
