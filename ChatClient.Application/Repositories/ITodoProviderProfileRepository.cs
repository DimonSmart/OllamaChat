using ChatClient.Domain.Models;

namespace ChatClient.Application.Repositories;

public interface ITodoProviderProfileRepository
{
    Task<IReadOnlyCollection<TodoProviderProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAllAsync(List<TodoProviderProfile> profiles, CancellationToken cancellationToken = default);
}
