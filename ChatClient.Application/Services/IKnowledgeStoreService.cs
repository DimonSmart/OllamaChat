using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IKnowledgeStoreService
{
    Task<IReadOnlyCollection<KnowledgeStore>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<KnowledgeStore?> GetAsync(Guid storeId, CancellationToken cancellationToken = default);
    Task<KnowledgeStore> CreateAsync(string name, string? description, CancellationToken cancellationToken = default);
    Task UpdateAsync(KnowledgeStore store, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid storeId, CancellationToken cancellationToken = default);
    Task AddOrUpdateDocumentAsync(Guid storeId, KnowledgeDocument document, CancellationToken cancellationToken = default);
    Task DeleteDocumentAsync(Guid storeId, Guid documentId, CancellationToken cancellationToken = default);
}
