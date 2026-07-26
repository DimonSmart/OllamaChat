using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using System.Security.Cryptography;
using System.Text;

namespace ChatClient.Api.Services.Rag;

public sealed class KnowledgeStoreService(
    IKnowledgeStoreRepository repository,
    IAgentTemplateService agents,
    IKnowledgeIndexBackgroundService indexer) : IKnowledgeStoreService
{
    public Task<IReadOnlyCollection<KnowledgeStore>> GetAllAsync(CancellationToken cancellationToken = default) => repository.GetAllAsync(cancellationToken);
    public async Task<KnowledgeStore?> GetAsync(Guid storeId, CancellationToken cancellationToken = default) =>
        (await repository.GetAllAsync(cancellationToken)).FirstOrDefault(x => x.Id == storeId);

    public async Task<KnowledgeStore> CreateAsync(string name, string? description, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Knowledge Store name is required.", nameof(name));
        var stores = (await repository.GetAllAsync(cancellationToken)).ToList();
        var store = new KnowledgeStore { Name = name.Trim(), Description = description?.Trim() };
        stores.Add(store);
        await repository.SaveAllAsync(stores, cancellationToken);
        return store;
    }

    public async Task UpdateAsync(KnowledgeStore store, CancellationToken cancellationToken = default)
    {
        var stores = (await repository.GetAllAsync(cancellationToken)).ToList();
        var index = stores.FindIndex(x => x.Id == store.Id);
        if (index < 0)
            throw new KeyNotFoundException($"Knowledge Store '{store.Id}' was not found.");
        if (store.Index.IndexedConfiguration is not null && !store.Configuration.Equals(store.Index.IndexedConfiguration) && store.Index.State == KnowledgeStoreIndexState.Ready)
            store.Index.State = KnowledgeStoreIndexState.Outdated;
        stores[index] = store;
        await repository.SaveAllAsync(stores, cancellationToken);
        if (store.Index.State is KnowledgeStoreIndexState.NotIndexed or KnowledgeStoreIndexState.Outdated or KnowledgeStoreIndexState.Failed)
            indexer.RequestRebuild();
    }

    public async Task DeleteAsync(Guid storeId, CancellationToken cancellationToken = default)
    {
        var stores = (await repository.GetAllAsync(cancellationToken)).ToList();
        if (!stores.RemoveAll(x => x.Id == storeId).Equals(1))
            return;
        await repository.SaveAllAsync(stores, cancellationToken);
        foreach (var agent in await agents.GetAllAsync())
            if (agent.KnowledgeStoreIds.Remove(storeId))
                await agents.UpdateAsync(agent);
        await indexer.DeleteStoreVectorsAsync(storeId, cancellationToken);
    }

    public async Task AddOrUpdateDocumentAsync(Guid storeId, KnowledgeDocument document, CancellationToken cancellationToken = default)
    {
        var store = await GetRequiredAsync(storeId, cancellationToken);
        if (string.IsNullOrWhiteSpace(document.FileName))
            throw new ArgumentException("File name is required.", nameof(document));
        document.SourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.Content)));
        document.UpdatedUtc = DateTime.UtcNow;
        var existing = store.Documents.FindIndex(x => x.Id == document.Id || x.FileName.Equals(document.FileName, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        { document.Id = store.Documents[existing].Id; store.Documents[existing] = document; }
        else
            store.Documents.Add(document);
        store.Index.State = store.Index.State == KnowledgeStoreIndexState.Ready ? KnowledgeStoreIndexState.Outdated : store.Index.State;
        await UpdateAsync(store, cancellationToken);
    }

    public async Task DeleteDocumentAsync(Guid storeId, Guid documentId, CancellationToken cancellationToken = default)
    {
        var store = await GetRequiredAsync(storeId, cancellationToken);
        if (store.Documents.RemoveAll(x => x.Id == documentId) == 0)
            return;
        await UpdateAsync(store, cancellationToken);
        await indexer.DeleteDocumentVectorsAsync(storeId, documentId, cancellationToken);
    }

    private async Task<KnowledgeStore> GetRequiredAsync(Guid id, CancellationToken ct) => await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Knowledge Store '{id}' was not found.");
}
