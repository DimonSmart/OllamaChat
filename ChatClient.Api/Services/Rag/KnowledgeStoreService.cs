using ChatClient.Application.Helpers;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using System.Security.Cryptography;
using System.Text;

namespace ChatClient.Api.Services.Rag;

public sealed class KnowledgeStoreService(
    IKnowledgeStoreRepository repository,
    IKnowledgeDocumentStorage documents,
    IAgentTemplateService agents,
    IUserSettingsService settings,
    IKnowledgeIndexBackgroundService indexer,
    ILogger<KnowledgeStoreService> logger) : IKnowledgeStoreService
{
    public Task<IReadOnlyCollection<KnowledgeStore>> GetAllAsync(CancellationToken ct = default) => repository.GetAllAsync(ct);
    public async Task<KnowledgeStore?> GetAsync(Guid id, CancellationToken ct = default) => (await repository.GetAllAsync(ct)).FirstOrDefault(x => x.Id == id);

    public async Task<KnowledgeStore> CreateAsync(string name, string? description, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Knowledge Store name is required.", nameof(name));
        var app = await settings.GetSettingsAsync(ct);
        var model = ModelSelectionHelper.GetEffectiveEmbeddingModel(app.Embedding.Model, app.DefaultModel, "Knowledge Store creation", logger);
        var store = new KnowledgeStore { Name = name.Trim(), Description = description?.Trim(), Configuration = new KnowledgeStoreIndexConfiguration { ServerId = model.ServerId, Model = model.ModelName } };
        await repository.SaveAsync(store, ct);
        return store;
    }

    public async Task UpdateAsync(KnowledgeStore store, CancellationToken ct = default)
    {
        var current = await RequiredAsync(store.Id, ct);
        if (store.Index.IndexedConfiguration is not null && !store.Configuration.Equals(store.Index.IndexedConfiguration) && store.Index.State == KnowledgeStoreIndexState.Ready)
            store.Index.State = KnowledgeStoreIndexState.Outdated;
        await repository.SaveAsync(store, ct);
        if (store.Index.State is KnowledgeStoreIndexState.NotIndexed or KnowledgeStoreIndexState.Outdated or KnowledgeStoreIndexState.Failed)
            indexer.RequestRebuild();
    }

    public async Task DeleteAsync(Guid storeId, CancellationToken ct = default)
    {
        var store = await GetAsync(storeId, ct);
        if (store is null)
            return;
        var dimension = store.Index.IndexedConfiguration?.Dimensions ?? 0;
        await indexer.DeleteStoreVectorsAsync(storeId, dimension, ct);
        await documents.DeleteStoreAsync(storeId, ct);
        await repository.DeleteAsync(storeId, ct);
        foreach (var agent in await agents.GetAllAsync())
            if (agent.KnowledgeStoreIds.Remove(storeId))
                await agents.UpdateAsync(agent);
    }

    public async Task AddOrUpdateDocumentAsync(Guid storeId, KnowledgeDocument document, CancellationToken ct = default)
    {
        var store = await RequiredAsync(storeId, ct);
        if (string.IsNullOrWhiteSpace(document.FileName))
            throw new ArgumentException("File name is required.", nameof(document));
        var content = document.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Document content is required.", nameof(document));
        var existing = store.Documents.FirstOrDefault(x => x.Id == document.Id || x.FileName.Equals(document.FileName, StringComparison.OrdinalIgnoreCase));
        document.Id = existing?.Id ?? document.Id;
        document.SourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        document.Size = Encoding.UTF8.GetByteCount(content);
        document.UpdatedUtc = DateTime.UtcNow;
        await documents.WriteAsync(store.Id, document.Id, content, ct);
        if (existing is null)
            store.Documents.Add(document);
        else
        { var index = store.Documents.IndexOf(existing); store.Documents[index] = document; }
        MarkOutdated(store);
        await UpdateAsync(store, ct);
    }

    public async Task DeleteDocumentAsync(Guid storeId, Guid documentId, CancellationToken ct = default)
    {
        var store = await RequiredAsync(storeId, ct);
        if (store.Documents.RemoveAll(x => x.Id == documentId) == 0)
            return;
        await documents.DeleteAsync(storeId, documentId, ct);
        MarkOutdated(store);
        await UpdateAsync(store, ct);
        await indexer.DeleteDocumentVectorsAsync(storeId, documentId, ct);
    }
    private static void MarkOutdated(KnowledgeStore store) { if (store.Index.State == KnowledgeStoreIndexState.Ready) store.Index.State = KnowledgeStoreIndexState.Outdated; }
    private async Task<KnowledgeStore> RequiredAsync(Guid id, CancellationToken ct) => await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Knowledge Store '{id}' was not found.");
}
