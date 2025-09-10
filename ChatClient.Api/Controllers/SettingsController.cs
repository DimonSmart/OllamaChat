using ChatClient.Domain.Models;
using ChatClient.Application.Services;

using Microsoft.AspNetCore.Mvc;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly IUserSettingsService _settingsService;

    public SettingsController(IUserSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    [HttpGet]
    public async Task<ActionResult<UserSettings>> GetSettings(CancellationToken cancellationToken)
    {
        var settings = await _settingsService.GetSettingsAsync(cancellationToken);
        return Ok(settings);
    }

    [HttpPost]
    public async Task<ActionResult> SaveSettings([FromBody] UserSettings settings, CancellationToken cancellationToken)
    {
        await _settingsService.SaveSettingsAsync(settings, cancellationToken);
        return Ok();
    }
}
