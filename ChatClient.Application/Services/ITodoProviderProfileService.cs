using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface ITodoProviderProfileService
{
    Task<IReadOnlyCollection<TodoProviderProfile>> GetAllAsync();
    Task<TodoProviderProfile?> GetByIdAsync(Guid id);
    Task CreateAsync(TodoProviderProfile profile);
    Task UpdateAsync(TodoProviderProfile profile);
    Task DeleteAsync(Guid id);
}
