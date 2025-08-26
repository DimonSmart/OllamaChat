using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ModelsController(
    IOllamaClientService ollamaService,
    IOpenAIClientService openAIService,
    IUserSettingsService userSettingsService,
    ILogger<ModelsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<object>> GetModels([FromQuery] Guid? serverId = null)
    {
        try
        {
            if (serverId == null || serverId == Guid.Empty)
                throw new ArgumentException("ServerId is required and cannot be empty");

            var serverType = await GetServerTypeAsync(serverId);

            if (serverType == ServerType.ChatGpt)
            {
                var models = await openAIService.GetAvailableModelsAsync(serverId.Value);
                return Ok(models.Select(m => new { Id = m, Name = m }));
            }
            else
            {
                var models = await ollamaService.GetModelsAsync(serverId.Value);
                return Ok(models);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving models for serverId: {ServerId}", serverId);
            return StatusCode(500, "An error occurred while retrieving models");
        }
    }

    private async Task<ServerType> GetServerTypeAsync(Guid? serverId)
    {
        var server = await LlmServerConfigHelper.GetServerConfigAsync(userSettingsService, serverId);
        return server?.ServerType ?? ServerType.Ollama;
    }
}
