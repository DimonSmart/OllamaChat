using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using Microsoft.AspNetCore.Mvc;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SystemPromptsController : ControllerBase
{
    private readonly ISystemPromptService _agentService;
    private readonly ILogger<SystemPromptsController> _logger;

    public SystemPromptsController(ISystemPromptService agentService, ILogger<SystemPromptsController> logger)
    {
        _agentService = agentService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<SystemPrompt>>> GetAgents()
    {
        try
        {
            var agents = await _agentService.GetAllPromptsAsync();
            return Ok(agents);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving all agents");
            return StatusCode(500, "An error occurred while retrieving agents");
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<SystemPrompt>> GetAgentById(Guid id)
    {
        try
        {
            var agent = await _agentService.GetPromptByIdAsync(id);
            if (agent == null)
            {
                return NotFound($"Agent with ID {id} not found");
            }

            return Ok(agent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving agent with ID {Id}", id);
            return StatusCode(500, "An error occurred while retrieving the agent");
        }
    }

    [HttpPost]
    public async Task<ActionResult<SystemPrompt>> CreateAgent([FromBody] SystemPrompt agent)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(agent.Name))
            {
                return BadRequest("Agent name is required");
            }

            if (string.IsNullOrWhiteSpace(agent.Content))
            {
                return BadRequest("Agent prompt content is required");
            }

            var createdAgent = await _agentService.CreatePromptAsync(agent);
            return CreatedAtAction(nameof(GetAgentById), new { id = createdAgent.Id }, createdAgent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating agent");
            return StatusCode(500, "An error occurred while creating the agent");
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<SystemPrompt>> UpdateAgent(Guid id, [FromBody] SystemPrompt agent)
    {
        try
        {
            if (id != agent.Id)
            {
                return BadRequest("ID in URL does not match ID in request body");
            }

            if (string.IsNullOrWhiteSpace(agent.Name))
            {
                return BadRequest("Agent name is required");
            }

            if (string.IsNullOrWhiteSpace(agent.Content))
            {
                return BadRequest("Agent prompt content is required");
            }

            var existingAgent = await _agentService.GetPromptByIdAsync(id);
            if (existingAgent == null)
            {
                return NotFound($"Agent with ID {id} not found");
            }

            var updatedAgent = await _agentService.UpdatePromptAsync(agent);
            return Ok(updatedAgent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating agent with ID {Id}", id);
            return StatusCode(500, "An error occurred while updating the agent");
        }
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteAgent(Guid id)
    {
        try
        {
            var existingAgent = await _agentService.GetPromptByIdAsync(id);
            if (existingAgent == null)
            {
                return NotFound($"Agent with ID {id} not found");
            }

            await _agentService.DeletePromptAsync(id);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting agent with ID {Id}", id);
            return StatusCode(500, "An error occurred while deleting the agent");
        }
    }
}
