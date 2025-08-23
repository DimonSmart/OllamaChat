using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using Microsoft.AspNetCore.Mvc;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly IUserSettingsService _settingsService;
    private readonly IEmbeddingModelChangeService _changeService;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(IUserSettingsService settingsService, IEmbeddingModelChangeService changeService, ILogger<SettingsController> logger)
    {
        _settingsService = settingsService;
        _changeService = changeService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<UserSettings>> GetSettings()
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            return Ok(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving user settings");
            return StatusCode(500, "An error occurred while retrieving settings");
        }
    }

    [HttpPost]
    public async Task<ActionResult> SaveSettings([FromBody] UserSettings settings)
    {
        try
        {
            var existing = await _settingsService.GetSettingsAsync();
            await _settingsService.SaveSettingsAsync(settings);
            if (!string.Equals(existing.EmbeddingModelName, settings.EmbeddingModelName, StringComparison.OrdinalIgnoreCase))
                await _changeService.HandleChangeAsync();
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving user settings");
            return StatusCode(500, "An error occurred while saving settings");
        }
    }
}
