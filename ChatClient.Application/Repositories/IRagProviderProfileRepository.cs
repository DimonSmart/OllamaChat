using ChatClient.Domain.Models;

namespace ChatClient.Application.Repositories;

public interface IRagProviderProfileRepository
{
    Task<IReadOnlyCollection<RagProviderProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAllAsync(List<RagProviderProfile> profiles, CancellationToken cancellationToken = default);
}
