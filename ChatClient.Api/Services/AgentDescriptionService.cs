using ChatClient.Domain.Models;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;

namespace ChatClient.Api.Services;

public class AgentDescriptionService(IAgentDescriptionRepository repository) : IAgentDescriptionService
{
    private readonly IAgentDescriptionRepository _repository = repository;

    public async Task<List<AgentDescription>> GetAllAsync()
    {
        var agents = await _repository.GetAllAsync();
        var updated = false;
        foreach (var agent in agents.Where(a => a.Id == Guid.Empty))
        {
            agent.Id = Guid.NewGuid();
            updated = true;
        }
        if (updated)
            await _repository.SaveAllAsync(agents);
        return agents;
    }

    public async Task<AgentDescription?> GetByIdAsync(Guid id)
    {
        var agents = await _repository.GetAllAsync();
        return agents.FirstOrDefault(p => p.Id == id);
    }

    public async Task CreateAsync(AgentDescription agentDescription)
    {
        var agents = await _repository.GetAllAsync();
        if (agentDescription.Id == Guid.Empty)
            agentDescription.Id = Guid.NewGuid();
        agentDescription.CreatedAt = DateTime.UtcNow;
        agentDescription.UpdatedAt = DateTime.UtcNow;
        agents.Add(agentDescription);
        await _repository.SaveAllAsync(agents);
    }

    public async Task UpdateAsync(AgentDescription agentDescription)
    {
        var agents = await _repository.GetAllAsync();
        var index = agents.FindIndex(p => p.Id == agentDescription.Id);
        if (index == -1)
            throw new KeyNotFoundException($"Agent with ID {agentDescription.Id} not found");
        agentDescription.UpdatedAt = DateTime.UtcNow;
        agents[index] = agentDescription;
        await _repository.SaveAllAsync(agents);
    }

    public async Task DeleteAsync(Guid id)
    {
        var agents = await _repository.GetAllAsync();
        var existing = agents.FirstOrDefault(p => p.Id == id) ??
                       throw new KeyNotFoundException($"Agent with ID {id} not found");
        agents.Remove(existing);
        await _repository.SaveAllAsync(agents);
    }

    public AgentDescription GetDefaultAgentDescription() => new()
    {
        Id = Guid.NewGuid(),
        AgentName = "Default Assistant",
        Content = "You are a helpful AI assistant. Please format your responses using Markdown."
    };
}

