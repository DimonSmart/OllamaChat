using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public sealed class KnowledgeStoreRepository : IKnowledgeStoreRepository
{
    private readonly JsonFileRepository<List<KnowledgeStore>> _repository;
    public KnowledgeStoreRepository(IConfiguration configuration, ILogger<KnowledgeStoreRepository> logger) =>
        _repository = new JsonFileRepository<List<KnowledgeStore>>(
            StoragePathResolver.ResolveUserPath(configuration, configuration["KnowledgeStores:FilePath"], "UserData/knowledge_stores.json"), logger);
    public async Task<IReadOnlyCollection<KnowledgeStore>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _repository.ReadAsync(cancellationToken) ?? [];
    public Task SaveAllAsync(List<KnowledgeStore> stores, CancellationToken cancellationToken = default) => _repository.WriteAsync(stores, cancellationToken);
}
