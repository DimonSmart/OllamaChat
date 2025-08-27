using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ServerTestController(
    IServerConnectionTestService connectionTestService,
    IUserSettingsService userSettingsService,
    ILogger<ServerTestController> logger) : ControllerBase
{
    [HttpPost("test-connection")]
    public async Task<ActionResult<ServerConnectionTestResult>> TestConnection([FromBody] LlmServerConfig server, CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation("Testing connection to server {ServerName} of type {ServerType}", server.Name, server.ServerType);

            var result = await connectionTestService.TestConnectionAsync(server, cancellationToken);

            if (result.IsSuccessful)
            {
                logger.LogInformation("Connection test successful for server {ServerName}", server.Name);
                return Ok(result);
            }
            else
            {
                logger.LogWarning("Connection test failed for server {ServerName}: {ErrorMessage}", server.Name, result.ErrorMessage);
                return BadRequest(result);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error testing connection to server {ServerName}", server.Name);
            return StatusCode(500, ServerConnectionTestResult.Failure($"Internal server error: {ex.Message}"));
        }
    }

    [HttpPost("test-connection/{serverId:guid}")]
    public async Task<ActionResult<ServerConnectionTestResult>> TestConnectionById(Guid serverId, CancellationToken cancellationToken = default)
    {
        try
        {
            var server = await LlmServerConfigHelper.GetServerConfigAsync(userSettingsService, serverId);
            if (server == null)
            {
                return NotFound(ServerConnectionTestResult.Failure("Server configuration not found"));
            }

            var result = await connectionTestService.TestConnectionAsync(server, cancellationToken);

            if (result.IsSuccessful)
            {
                return Ok(result);
            }
            else
            {
                return BadRequest(result);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error testing connection to server {ServerId}", serverId);
            return StatusCode(500, ServerConnectionTestResult.Failure($"Internal server error: {ex.Message}"));
        }
    }
}
