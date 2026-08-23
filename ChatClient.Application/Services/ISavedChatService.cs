using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface ISavedChatService
{
    Task<IReadOnlyList<SavedChatSummary>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<SavedChatDocument?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<SavedChatDocument?> GetAsync(string storageRoot, Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(SavedChatDocument chat, CancellationToken cancellationToken = default);
    Task SaveCheckpointAsync(SavedChatDocument chat, CancellationToken cancellationToken = default);
    Task RenameAsync(Guid id, string title, CancellationToken cancellationToken = default);
    Task<bool> IsAutoSaveEnabledAsync(CancellationToken cancellationToken = default);
}
