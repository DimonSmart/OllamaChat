using ChatClient.Shared.Models;

namespace ChatClient.Shared.Services;

public interface IAgentDescriptionService
{
    Task<List<AgentDescription>> GetAllAsync();
    Task<AgentDescription?> GetByIdAsync(Guid id);
    Task<AgentDescription> CreateAsync(AgentDescription prompt);
    Task<AgentDescription> UpdateAsync(AgentDescription prompt);
    Task DeleteAsync(Guid id);
    AgentDescription GetDefaultAgentDescription();
}
