using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public sealed class KnowledgeStoreRepository : IKnowledgeStoreRepository
{
    private readonly JsonFileRepository<List<KnowledgeStore>> _repository;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public KnowledgeStoreRepository(IConfiguration configuration, ILogger<KnowledgeStoreRepository> logger) =>
        _repository = new JsonFileRepository<List<KnowledgeStore>>(
            StoragePathResolver.ResolveUserPath(configuration, configuration["KnowledgeStores:FilePath"], "UserData/knowledge_stores.json"), logger);
    public async Task<IReadOnlyCollection<KnowledgeStore>> GetAllAsync(CancellationToken cancellationToken = default) =>
        (await _repository.ReadAsync(cancellationToken) ?? []).Select(CloneWithoutPayload).ToList();
    public Task SaveAsync(KnowledgeStore store, CancellationToken cancellationToken = default) => ModifyAsync(stores =>
    {
        var index = stores.FindIndex(x => x.Id == store.Id);
        if (index < 0)
            stores.Add(CloneWithoutPayload(store));
        else
            stores[index] = CloneWithoutPayload(store);
    }, cancellationToken);
    public Task DeleteAsync(Guid storeId, CancellationToken cancellationToken = default) => ModifyAsync(stores => stores.RemoveAll(x => x.Id == storeId), cancellationToken);
    private async Task ModifyAsync(Action<List<KnowledgeStore>> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        { var stores = await _repository.ReadAsync(ct) ?? []; change(stores); await _repository.WriteAsync(stores, ct); }
        finally { _gate.Release(); }
    }
    private static KnowledgeStore CloneWithoutPayload(KnowledgeStore store) => new()
    {
        Id = store.Id,
        Name = store.Name,
        Description = store.Description,
        Configuration = store.Configuration.Clone(),
        Index = new KnowledgeStoreIndexMetadata { State = store.Index.State, IndexedConfiguration = store.Index.IndexedConfiguration?.Clone(), LastError = store.Index.LastError, CompletedUtc = store.Index.CompletedUtc },
        Documents = store.Documents.Select(d => new KnowledgeDocument { Id = d.Id, FileName = d.FileName, SourceHash = d.SourceHash, Size = d.Size, UpdatedUtc = d.UpdatedUtc }).ToList()
    };
}
