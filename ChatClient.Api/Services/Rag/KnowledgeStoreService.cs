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
    IKnowledgeDocumentIngestionService ingestion,
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

    public async Task RequestReindexAsync(Guid storeId, CancellationToken ct = default)
    {
        var store = await RequiredAsync(storeId, ct);
        store.Index.ForceRebuild = true;
        if (store.Index.State != KnowledgeStoreIndexState.Indexing)
            store.Index.State = KnowledgeStoreIndexState.Outdated;
        await repository.SaveAsync(store, ct);
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

    public async Task AddOrUpdateDocumentAsync(Guid storeId, string fileName, Stream content, string? contentType = null, CancellationToken ct = default)
    {
        var store = await RequiredAsync(storeId, ct);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File name is required.", nameof(fileName));
        var prepared = await ingestion.PrepareAsync(fileName, content, contentType, ct);
        var existing = store.Documents.FirstOrDefault(x => x.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        var document = new KnowledgeDocument
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            FileName = fileName.Trim(),
            ContentType = contentType,
            SourceHash = Convert.ToHexString(SHA256.HashData(prepared.Source)),
            Size = prepared.Source.LongLength,
            UpdatedUtc = DateTime.UtcNow,
            IndexedSourceHash = existing?.IndexedSourceHash
        };
        if (existing?.SourceHash == document.SourceHash)
            return;
        await using var source = new MemoryStream(prepared.Source, writable: false);
        await documents.WriteAsync(store.Id, document.Id, document.FileName, source, prepared.CanonicalMarkdown, ct);
        if (existing is null)
            store.Documents.Add(document);
        else
        { var index = store.Documents.IndexOf(existing); store.Documents[index] = document; }
        if (store.Index.State == KnowledgeStoreIndexState.Ready)
            store.Index.State = KnowledgeStoreIndexState.Outdated;
        await UpdateAsync(store, ct);
    }

    public async Task DeleteDocumentAsync(Guid storeId, Guid documentId, CancellationToken ct = default)
    {
        var store = await RequiredAsync(storeId, ct);
        if (store.Documents.RemoveAll(x => x.Id == documentId) == 0)
            return;
        await documents.DeleteAsync(storeId, documentId, ct);
        await indexer.DeleteDocumentVectorsAsync(storeId, documentId, ct);
        if (store.Documents.Count == 0)
            store.Index.State = KnowledgeStoreIndexState.NotIndexed;
        await UpdateAsync(store, ct);
    }
    private async Task<KnowledgeStore> RequiredAsync(Guid id, CancellationToken ct) => await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Knowledge Store '{id}' was not found.");
}
