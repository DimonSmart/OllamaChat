using ChatClient.Api.Services;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ServerTestController(
    IServerConnectionTestService connectionTestService,
    IUserSettingsService userSettingsService,
    ILlmServerConfigService llmServerConfigService,
    ILogger<ServerTestController> logger) : ControllerBase
{
    [HttpPost("test-connection")]
    public async Task<ActionResult<ServerConnectionTestResult>> TestConnection([FromBody] LlmServerConfig server, CancellationToken cancellationToken = default)
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

    [HttpPost("test-connection/{serverId:guid}")]
    public async Task<ActionResult<ServerConnectionTestResult>> TestConnectionById(Guid serverId, CancellationToken cancellationToken = default)
    {
        var server = await LlmServerConfigHelper.GetServerConfigAsync(llmServerConfigService, userSettingsService, serverId);
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
}
