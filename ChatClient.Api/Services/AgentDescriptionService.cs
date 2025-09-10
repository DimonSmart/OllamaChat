using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public class AgentDescriptionService(IAgentDescriptionRepository repository) : IAgentDescriptionService
{
    private readonly IAgentDescriptionRepository _repository = repository;

    public async Task<IReadOnlyCollection<AgentDescription>> GetAllAsync()
    {
        return await _repository.GetAllAsync();
    }

    public async Task<AgentDescription?> GetByIdAsync(Guid id)
    {
        var agents = await _repository.GetAllAsync();
        return agents.FirstOrDefault(p => p.Id == id);
    }

    public async Task CreateAsync(AgentDescription agentDescription)
    {
        var agents = (await _repository.GetAllAsync()).ToList();
        agentDescription.CreatedAt = DateTime.UtcNow;
        agentDescription.UpdatedAt = DateTime.UtcNow;
        agents.Add(agentDescription);
        await _repository.SaveAllAsync(agents);
    }

    public async Task UpdateAsync(AgentDescription agentDescription)
    {
        var agents = (await _repository.GetAllAsync()).ToList();
        var index = agents.FindIndex(p => p.Id == agentDescription.Id);
        if (index == -1)
            throw new KeyNotFoundException($"Agent with ID {agentDescription.Id} not found");
        agentDescription.UpdatedAt = DateTime.UtcNow;
        agents[index] = agentDescription;
        await _repository.SaveAllAsync(agents);
    }

    public async Task DeleteAsync(Guid id)
    {
        var agents = (await _repository.GetAllAsync()).ToList();
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

