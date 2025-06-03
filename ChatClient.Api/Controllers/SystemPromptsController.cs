using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using Microsoft.AspNetCore.Mvc;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SystemPromptsController : ControllerBase
{
    private readonly ISystemPromptService _systemPromptService;
    private readonly ILogger<SystemPromptsController> _logger;

    public SystemPromptsController(ISystemPromptService systemPromptService, ILogger<SystemPromptsController> logger)
    {
        _systemPromptService = systemPromptService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<SystemPrompt>>> GetAllPrompts()
    {
        try
        {
            var prompts = await _systemPromptService.GetAllPromptsAsync();
            return Ok(prompts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving all system prompts");
            return StatusCode(500, "An error occurred while retrieving system prompts");
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<SystemPrompt>> GetPromptById(Guid id)
    {
        try
        {
            var prompt = await _systemPromptService.GetPromptByIdAsync(id);
            if (prompt == null)
            {
                return NotFound($"System prompt with ID {id} not found");
            }

            return Ok(prompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving system prompt with ID {Id}", id);
            return StatusCode(500, "An error occurred while retrieving the system prompt");
        }
    }

    [HttpPost]
    public async Task<ActionResult<SystemPrompt>> CreatePrompt([FromBody] SystemPrompt prompt)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(prompt.Name))
            {
                return BadRequest("Prompt name is required");
            }

            if (string.IsNullOrWhiteSpace(prompt.Content))
            {
                return BadRequest("Prompt content is required");
            }

            var createdPrompt = await _systemPromptService.CreatePromptAsync(prompt);
            return CreatedAtAction(nameof(GetPromptById), new { id = createdPrompt.Id }, createdPrompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating system prompt");
            return StatusCode(500, "An error occurred while creating the system prompt");
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<SystemPrompt>> UpdatePrompt(Guid id, [FromBody] SystemPrompt prompt)
    {
        try
        {
            if (id != prompt.Id)
            {
                return BadRequest("ID in URL does not match ID in request body");
            }

            if (string.IsNullOrWhiteSpace(prompt.Name))
            {
                return BadRequest("Prompt name is required");
            }

            if (string.IsNullOrWhiteSpace(prompt.Content))
            {
                return BadRequest("Prompt content is required");
            }

            var existingPrompt = await _systemPromptService.GetPromptByIdAsync(id);
            if (existingPrompt == null)
            {
                return NotFound($"System prompt with ID {id} not found");
            }

            var updatedPrompt = await _systemPromptService.UpdatePromptAsync(prompt);
            return Ok(updatedPrompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating system prompt with ID {Id}", id);
            return StatusCode(500, "An error occurred while updating the system prompt");
        }
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeletePrompt(Guid id)
    {
        try
        {
            var existingPrompt = await _systemPromptService.GetPromptByIdAsync(id);
            if (existingPrompt == null)
            {
                return NotFound($"System prompt with ID {id} not found");
            }

            await _systemPromptService.DeletePromptAsync(id);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting system prompt with ID {Id}", id);
            return StatusCode(500, "An error occurred while deleting the system prompt");
        }
    }
}
