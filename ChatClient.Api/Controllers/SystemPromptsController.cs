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
            _logger.LogError(ex, "Error retrieving all agents");
            return StatusCode(500, "An error occurred while retrieving agents");
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
                return NotFound($"Agent with ID {id} not found");
            }

            return Ok(prompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving agent with ID {Id}", id);
            return StatusCode(500, "An error occurred while retrieving the agent");
        }
    }

    [HttpPost]
    public async Task<ActionResult<SystemPrompt>> CreatePrompt([FromBody] SystemPrompt prompt)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(prompt.Name))
            {
                return BadRequest("Agent name is required");
            }

            if (string.IsNullOrWhiteSpace(prompt.Content))
            {
                return BadRequest("Agent prompt content is required");
            }

            var createdPrompt = await _systemPromptService.CreatePromptAsync(prompt);
            return CreatedAtAction(nameof(GetPromptById), new { id = createdPrompt.Id }, createdPrompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating agent");
            return StatusCode(500, "An error occurred while creating the agent");
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
                return BadRequest("Agent name is required");
            }

            if (string.IsNullOrWhiteSpace(prompt.Content))
            {
                return BadRequest("Agent prompt content is required");
            }

            var existingPrompt = await _systemPromptService.GetPromptByIdAsync(id);
            if (existingPrompt == null)
            {
                return NotFound($"Agent with ID {id} not found");
            }

            var updatedPrompt = await _systemPromptService.UpdatePromptAsync(prompt);
            return Ok(updatedPrompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating agent with ID {Id}", id);
            return StatusCode(500, "An error occurred while updating the agent");
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
                return NotFound($"Agent with ID {id} not found");
            }

            await _systemPromptService.DeletePromptAsync(id);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting agent with ID {Id}", id);
            return StatusCode(500, "An error occurred while deleting the agent");
        }
    }
}
