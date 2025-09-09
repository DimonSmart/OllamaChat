using ChatClient.Shared.Models;

namespace ChatClient.Shared.Services;

public interface IAgentDescriptionService
{
    Task<List<AgentDescription>> GetAllAsync();
    Task<AgentDescription?> GetByIdAsync(Guid id);
    Task CreateAsync(AgentDescription agentDescription);
    Task UpdateAsync(AgentDescription agentDescription);
    Task DeleteAsync(Guid id);
    AgentDescription GetDefaultAgentDescription();
}
