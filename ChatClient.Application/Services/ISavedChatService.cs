using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface ISavedChatService
{
    Task<IReadOnlyCollection<SavedChat>> GetAllAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Returns saved chats matching the specified query in title or participant names.
    /// </summary>
    Task<IReadOnlyCollection<SavedChat>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<SavedChat?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(SavedChat savedChat, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
