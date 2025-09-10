namespace ChatClient.Application.Repositories;

using ChatClient.Domain.Models;

public interface ISavedChatRepository
{
    Task<List<SavedChat>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<SavedChat?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(SavedChat chat, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
