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
    public Task<IReadOnlyCollection<KnowledgeStore>> GetAllAsync(CancellationToken cancellationToken = default) => repository.GetAllAsync(cancellationToken);
    public async Task<KnowledgeStore?> GetAsync(Guid id, CancellationToken cancellationToken = default) => (await repository.GetAllAsync(cancellationToken)).FirstOrDefault(x => x.Id == id);

    public async Task<KnowledgeStore> CreateAsync(string name, string? description, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Knowledge Store name is required.", nameof(name));
        var app = await settings.GetSettingsAsync(cancellationToken);
        var model = ModelSelectionHelper.GetEffectiveEmbeddingModel(app.Embedding.Model, app.DefaultModel, "Knowledge Store creation", logger);
        var store = new KnowledgeStore { Name = name.Trim(), Description = description?.Trim(), Configuration = new KnowledgeStoreIndexConfiguration { ServerId = model.ServerId, Model = model.ModelName } };
        await repository.SaveAsync(store, cancellationToken);
        return store;
    }

    public async Task UpdateAsync(KnowledgeStore store, CancellationToken cancellationToken = default)
    {
        var current = await RequiredAsync(store.Id, cancellationToken);
        if (store.Index.IndexedConfiguration is not null && !store.Configuration.Equals(store.Index.IndexedConfiguration) && store.Index.State == KnowledgeStoreIndexState.Ready)
            store.Index.State = KnowledgeStoreIndexState.Outdated;
        await repository.SaveAsync(store, cancellationToken);
        if (store.Index.State is KnowledgeStoreIndexState.NotIndexed or KnowledgeStoreIndexState.Outdated)
            indexer.RequestRebuild();
    }

    public async Task RequestReindexAsync(Guid storeId, CancellationToken cancellationToken = default)
    {
        var store = await RequiredAsync(storeId, cancellationToken);
        store.Index.ForceRebuild = true;
        if (store.Index.State != KnowledgeStoreIndexState.Indexing)
            store.Index.State = KnowledgeStoreIndexState.Outdated;
        await repository.SaveAsync(store, cancellationToken);
        indexer.RequestRebuild();
    }

    public async Task DeleteAsync(Guid storeId, CancellationToken cancellationToken = default)
    {
        var store = await GetAsync(storeId, cancellationToken);
        if (store is null)
            return;
        var dimension = store.Index.IndexedConfiguration?.Dimensions ?? 0;
        await indexer.DeleteStoreVectorsAsync(storeId, dimension, cancellationToken);
        await documents.DeleteStoreAsync(storeId, cancellationToken);
        await repository.DeleteAsync(storeId, cancellationToken);
        foreach (var agent in await agents.GetAllAsync())
            if (agent.KnowledgeStoreIds.Remove(storeId))
                await agents.UpdateAsync(agent);
    }

    public async Task AddOrUpdateDocumentAsync(Guid storeId, string fileName, Stream content, string? contentType = null, CancellationToken cancellationToken = default)
    {
        var store = await RequiredAsync(storeId, cancellationToken);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File name is required.", nameof(fileName));
        var prepared = await ingestion.PrepareAsync(fileName, content, contentType, cancellationToken);
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
        await documents.WriteAsync(store.Id, document.Id, document.FileName, source, prepared.CanonicalMarkdown, cancellationToken);
        if (existing is null)
            store.Documents.Add(document);
        else
        { var index = store.Documents.IndexOf(existing); store.Documents[index] = document; }
        if (store.Index.State == KnowledgeStoreIndexState.Ready)
            store.Index.State = KnowledgeStoreIndexState.Outdated;
        await UpdateAsync(store, cancellationToken);
    }

    public async Task DeleteDocumentAsync(Guid storeId, Guid documentId, CancellationToken cancellationToken = default)
    {
        var store = await RequiredAsync(storeId, cancellationToken);
        if (store.Documents.RemoveAll(x => x.Id == documentId) == 0)
            return;
        await documents.DeleteAsync(storeId, documentId, cancellationToken);
        await indexer.DeleteDocumentVectorsAsync(storeId, documentId, cancellationToken);
        if (store.Documents.Count == 0)
            store.Index.State = KnowledgeStoreIndexState.NotIndexed;
        await UpdateAsync(store, cancellationToken);
    }
    private async Task<KnowledgeStore> RequiredAsync(Guid id, CancellationToken cancellationToken) => await GetAsync(id, cancellationToken) ?? throw new KeyNotFoundException($"Knowledge Store '{id}' was not found.");
}
