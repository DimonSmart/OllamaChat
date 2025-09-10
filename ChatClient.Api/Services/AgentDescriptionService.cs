using ChatClient.Api.Repositories;
using ChatClient.Shared.Constants;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

namespace ChatClient.Api.Services;

public class AgentDescriptionService : IAgentDescriptionService
{
    private readonly JsonFileRepository<List<AgentDescription>> _repository;

    public AgentDescriptionService(IConfiguration configuration, ILogger<AgentDescriptionService> logger)
    {
        var filePath = configuration["AgentDescriptions:FilePath"] ?? FilePathConstants.DefaultAgentDescriptionsFile;
        _repository = new JsonFileRepository<List<AgentDescription>>(filePath, logger);
    }

    public async Task<List<AgentDescription>> GetAllAsync()
    {
        var agents = await _repository.ReadAsync() ?? [];
        var updated = false;
        foreach (var agent in agents.Where(a => a.Id == Guid.Empty))
        {
            agent.Id = Guid.NewGuid();
            updated = true;
        }
        if (updated)
            await _repository.WriteAsync(agents);
        return agents;
    }

    public async Task<AgentDescription?> GetByIdAsync(Guid id)
    {
        var agents = await GetAllAsync();
        return agents.FirstOrDefault(p => p.Id == id);
    }

    public async Task CreateAsync(AgentDescription agentDescription)
    {
        await _repository.UpdateAsync(agents =>
        {
            if (agentDescription.Id == Guid.Empty)
                agentDescription.Id = Guid.NewGuid();
            agentDescription.CreatedAt = DateTime.UtcNow;
            agentDescription.UpdatedAt = DateTime.UtcNow;
            agents.Add(agentDescription);
            return Task.CompletedTask;
        }, []);
    }

    public async Task UpdateAsync(AgentDescription agentDescription)
    {
        await _repository.UpdateAsync(agents =>
        {
            var index = agents.FindIndex(p => p.Id == agentDescription.Id);
            if (index == -1)
                throw new KeyNotFoundException($"Agent with ID {agentDescription.Id} not found");
            agentDescription.UpdatedAt = DateTime.UtcNow;
            agents[index] = agentDescription;
            return Task.CompletedTask;
        }, []);
    }

    public async Task DeleteAsync(Guid id)
    {
        await _repository.UpdateAsync(agents =>
        {
            var existing = agents.FirstOrDefault(p => p.Id == id) ??
                           throw new KeyNotFoundException($"Agent with ID {id} not found");
            agents.Remove(existing);
            return Task.CompletedTask;
        }, []);
    }

    public AgentDescription GetDefaultAgentDescription() => new()
    {
        Id = Guid.NewGuid(),
        AgentName = "Default Assistant",
        Content = "You are a helpful AI assistant. Please format your responses using Markdown."
    };
}
