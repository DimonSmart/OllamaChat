using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IAgentDescriptionService
{
    Task<IReadOnlyCollection<AgentDescription>> GetAllAsync();
    Task<AgentDescription?> GetByIdAsync(Guid id);
    Task CreateAsync(AgentDescription agentDescription);
    Task UpdateAsync(AgentDescription agentDescription);
    Task DeleteAsync(Guid id);
    AgentDescription GetDefaultAgentDescription();
}
