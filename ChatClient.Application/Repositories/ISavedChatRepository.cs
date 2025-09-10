namespace ChatClient.Application.Repositories;

using ChatClient.Domain.Models;

public interface ISavedChatRepository
{
    Task<IReadOnlyCollection<SavedChat>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<SavedChat?> GetByIdAsync(Guid chatId, CancellationToken cancellationToken = default);
    Task SaveAsync(SavedChat chat, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid chatId, CancellationToken cancellationToken = default);
}
