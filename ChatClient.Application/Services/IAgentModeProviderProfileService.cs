using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IAgentModeProviderProfileService
{
    Task<IReadOnlyCollection<AgentModeProviderProfile>> GetAllAsync();
    Task<AgentModeProviderProfile?> GetByIdAsync(Guid id);
    Task CreateAsync(AgentModeProviderProfile profile);
    Task UpdateAsync(AgentModeProviderProfile profile);
    Task DeleteAsync(Guid id);
}
