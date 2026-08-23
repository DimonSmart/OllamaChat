using ChatClient.Domain.Models;

namespace ChatClient.Application.Repositories;

public interface ISavedChatRepository
{
    Task SaveAsync(string storageRoot, SavedChatDocument chat, CancellationToken cancellationToken = default);
    Task UpdateAsync(string storageRoot, Guid id, Func<SavedChatDocument, SavedChatDocument> update, CancellationToken cancellationToken = default);
    Task SaveCheckpointAsync(string storageRoot, SavedChatDocument chat, CancellationToken cancellationToken = default);
    Task<SavedChatDocument?> GetAsync(string storageRoot, Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SavedChatSummary>> GetAllAsync(string storageRoot, CancellationToken cancellationToken = default);
}
