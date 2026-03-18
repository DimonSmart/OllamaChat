using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public class AgentDescriptionService(IAgentDescriptionRepository repository) : IAgentDescriptionService
{
    private readonly IAgentDescriptionRepository _repository = repository;

    public async Task<IReadOnlyCollection<AgentDescription>> GetAllAsync()
    {
        return await GetNormalizedAgentsAsync();
    }

    public async Task<AgentDescription?> GetByIdAsync(Guid agentId)
    {
        var agents = await GetNormalizedAgentsAsync();
        return agents.FirstOrDefault(p => p.Id == agentId);
    }

    public async Task CreateAsync(AgentDescription agentDescription)
    {
        var agents = await GetNormalizedAgentsAsync();
        var usedIds = agents
            .Select(static agent => agent.Id)
            .Where(static id => id != Guid.Empty)
            .ToHashSet();

        if (agentDescription.Id == Guid.Empty || !usedIds.Add(agentDescription.Id))
        {
            agentDescription.Id = GenerateUniqueId(usedIds);
        }

        var now = DateTime.UtcNow;
        agentDescription.CreatedAt = now;
        agentDescription.UpdatedAt = now;
        agents.Add(agentDescription);
        await _repository.SaveAllAsync(agents);
    }

    public async Task UpdateAsync(AgentDescription agentDescription)
    {
        var agents = await GetNormalizedAgentsAsync();
        var index = agents.FindIndex(p => p.Id == agentDescription.Id);
        if (index == -1)
            throw new KeyNotFoundException($"Agent with ID {agentDescription.Id} not found");
        agentDescription.UpdatedAt = DateTime.UtcNow;
        agents[index] = agentDescription;
        await _repository.SaveAllAsync(agents);
    }

    public async Task DeleteAsync(Guid agentId)
    {
        var agents = await GetNormalizedAgentsAsync();
        var existing = agents.FirstOrDefault(p => p.Id == agentId) ??
                       throw new KeyNotFoundException($"Agent with ID {agentId} not found");
        agents.Remove(existing);
        await _repository.SaveAllAsync(agents);
    }

    private async Task<List<AgentDescription>> GetNormalizedAgentsAsync()
    {
        var agents = (await _repository.GetAllAsync()).ToList();
        if (!NormalizeIds(agents))
        {
            return agents;
        }

        await _repository.SaveAllAsync(agents);
        return agents;
    }

    private static bool NormalizeIds(List<AgentDescription> agents)
    {
        var usedIds = new HashSet<Guid>();
        var hasChanges = false;

        foreach (var agent in agents)
        {
            if (agent.Id != Guid.Empty && usedIds.Add(agent.Id))
            {
                continue;
            }

            agent.Id = GenerateUniqueId(usedIds);
            hasChanges = true;
        }

        return hasChanges;
    }

    private static Guid GenerateUniqueId(HashSet<Guid> usedIds)
    {
        Guid id;
        do
        {
            id = Guid.NewGuid();
        }
        while (!usedIds.Add(id));

        return id;
    }
}

