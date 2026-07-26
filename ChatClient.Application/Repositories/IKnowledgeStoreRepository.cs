using ChatClient.Domain.Models;

namespace ChatClient.Application.Repositories;

public interface IKnowledgeStoreRepository
{
    Task<IReadOnlyCollection<KnowledgeStore>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAllAsync(List<KnowledgeStore> stores, CancellationToken cancellationToken = default);
}
