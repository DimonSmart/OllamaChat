using ChatClient.Api.Services;
using ChatClient.Shared.Models;

using Microsoft.AspNetCore.Mvc;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ModelsController(IOllamaClientService ollamaService, ILogger<ModelsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<OllamaModel>>> GetModels()
    {
        try
        {
            var models = await ollamaService.GetModelsAsync();
            return Ok(models);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving Ollama models");
            return StatusCode(500, "An error occurred while retrieving models");
        }
    }
}
