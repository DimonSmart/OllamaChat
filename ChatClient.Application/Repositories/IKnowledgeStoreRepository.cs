using ChatClient.Domain.Models;

namespace ChatClient.Application.Repositories;

public interface IKnowledgeStoreRepository
{
    Task<IReadOnlyCollection<KnowledgeStore>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(KnowledgeStore store, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid storeId, CancellationToken cancellationToken = default);
}
