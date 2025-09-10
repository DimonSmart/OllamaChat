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

    [HttpGet("{agentId}")]
    public async Task<ActionResult<AgentDescription>> GetAgentById(Guid agentId)
    {
        var agent = await _agentService.GetByIdAsync(agentId);
        if (agent == null)
        {
            return NotFound($"Agent with ID {agentId} not found");
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
        return CreatedAtAction(nameof(GetAgentById), new { agentId = agent.Id }, agent);
    }

    [HttpPut("{agentId}")]
    public async Task<ActionResult<AgentDescription>> UpdateAgent(Guid agentId, [FromBody] AgentDescription agent)
    {
        if (agentId != agent.Id)
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

        var existingAgent = await _agentService.GetByIdAsync(agentId);
        if (existingAgent == null)
        {
            return NotFound($"Agent with ID {agentId} not found");
        }

        await _agentService.UpdateAsync(agent);
        return Ok(agent);
    }

    [HttpDelete("{agentId}")]
    public async Task<ActionResult> DeleteAgent(Guid agentId)
    {
        var existingAgent = await _agentService.GetByIdAsync(agentId);
        if (existingAgent == null)
        {
            return NotFound($"Agent with ID {agentId} not found");
        }

        await _agentService.DeleteAsync(agentId);
        return NoContent();
    }
}
