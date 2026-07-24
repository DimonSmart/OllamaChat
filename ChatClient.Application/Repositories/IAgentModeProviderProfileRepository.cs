using ChatClient.Domain.Models;

namespace ChatClient.Application.Repositories;

public interface IAgentModeProviderProfileRepository
{
    Task<IReadOnlyCollection<AgentModeProviderProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAllAsync(List<AgentModeProviderProfile> profiles, CancellationToken cancellationToken = default);
}
