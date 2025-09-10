using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AgentDescriptionsController : ControllerBase
{
    private readonly IAgentDescriptionService _agentService;

    public AgentDescriptionsController(IAgentDescriptionService agentService)
    {
        _agentService = agentService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<AgentDescription>>> GetAgents()
    {
        var agents = await _agentService.GetAllAsync();
        return Ok(agents);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<AgentDescription>> GetAgentById(Guid id)
    {
        var agent = await _agentService.GetByIdAsync(id);
        if (agent == null)
        {
            return NotFound($"Agent with ID {id} not found");
        }

        return Ok(agent);
    }

    [HttpPost]
    public async Task<ActionResult<AgentDescription>> CreateAgent([FromBody] AgentDescription agent)
    {
        if (string.IsNullOrWhiteSpace(agent.AgentName))
        {
            return BadRequest("Agent name is required");
        }

        if (string.IsNullOrWhiteSpace(agent.Content))
        {
            return BadRequest("Agent description content is required");
        }

        await _agentService.CreateAsync(agent);
        return CreatedAtAction(nameof(GetAgentById), new { id = agent.Id }, agent);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<AgentDescription>> UpdateAgent(Guid id, [FromBody] AgentDescription agent)
    {
        if (id != agent.Id)
        {
            return BadRequest("ID in URL does not match ID in request body");
        }

        if (string.IsNullOrWhiteSpace(agent.AgentName))
        {
            return BadRequest("Agent name is required");
        }

        if (string.IsNullOrWhiteSpace(agent.Content))
        {
            return BadRequest("Agent description content is required");
        }

        var existingAgent = await _agentService.GetByIdAsync(id);
        if (existingAgent == null)
        {
            return NotFound($"Agent with ID {id} not found");
        }

        await _agentService.UpdateAsync(agent);
        return Ok(agent);
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteAgent(Guid id)
    {
        var existingAgent = await _agentService.GetByIdAsync(id);
        if (existingAgent == null)
        {
            return NotFound($"Agent with ID {id} not found");
        }

        await _agentService.DeleteAsync(id);
        return NoContent();
    }
}
