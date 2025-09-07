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
    ILlmServerConfigService llmServerConfigService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<object>> GetModels([FromQuery] Guid? serverId = null)
    {
        if (serverId == null || serverId == Guid.Empty)
            return BadRequest("ServerId is required and cannot be empty");

        var serverType = await LlmServerConfigHelper.GetServerTypeAsync(llmServerConfigService, userSettingsService, serverId);

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
}
