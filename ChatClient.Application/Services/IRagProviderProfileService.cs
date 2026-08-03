using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IRagProviderProfileService
{
    Task<IReadOnlyCollection<RagProviderProfile>> GetAllAsync();
    Task<RagProviderProfile?> GetByIdAsync(Guid id);
    Task CreateAsync(RagProviderProfile profile);
    Task UpdateAsync(RagProviderProfile profile);
    Task DeleteAsync(Guid id);
}
