using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.Rag;

public sealed class KnowledgeIndexBackgroundService(IServiceScopeFactory scopes, ILogger<KnowledgeIndexBackgroundService> logger, IKnowledgeIndexProgressTracker progress) : BackgroundService, IKnowledgeIndexBackgroundService
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private bool _isFirstRebuild = true;
    public void RequestRebuild() { if (_signal.CurrentCount == 0) _signal.Release(); }
    public async Task DeleteStoreVectorsAsync(Guid id, int dimension, CancellationToken ct = default) { if (dimension > 0) { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<IKnowledgeIndex>().DeleteStoreAsync(id, dimension, ct); } }
    public async Task DeleteDocumentVectorsAsync(Guid id, Guid documentId, CancellationToken ct = default) { using var scope = scopes.CreateScope(); var store = await scope.ServiceProvider.GetRequiredService<IKnowledgeStoreService>().GetAsync(id, ct); var dimension = store?.Index.IndexedConfiguration?.Dimensions ?? 0; if (dimension > 0) await scope.ServiceProvider.GetRequiredService<IKnowledgeIndex>().DeleteDocumentAsync(id, documentId, dimension, ct); }
    protected override async Task ExecuteAsync(CancellationToken ct) { RequestRebuild(); while (!ct.IsCancellationRequested) { await _signal.WaitAsync(ct); await RebuildAsync(ct); } }
    private async Task RebuildAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var stores = scope.ServiceProvider.GetRequiredService<IKnowledgeStoreService>();
        var payloads = scope.ServiceProvider.GetRequiredService<IKnowledgeDocumentStorage>();
        var chunker = scope.ServiceProvider.GetRequiredService<IKnowledgeMarkdownChunker>();
        var embeddingGeneratorResolver = scope.ServiceProvider.GetRequiredService<IEmbeddingGeneratorResolver>();
        var knowledgeIndex = scope.ServiceProvider.GetRequiredService<IKnowledgeIndex>();
        var allStores = await stores.GetAllAsync(ct);
        if (_isFirstRebuild)
        {
            _isFirstRebuild = false;
            foreach (var store in allStores.Where(store => store.Index.State == KnowledgeStoreIndexState.Indexing))
            {
                store.Index.State = KnowledgeStoreIndexState.Outdated;
                await stores.UpdateAsync(store, ct);
            }
            allStores = await stores.GetAllAsync(ct);
        }
        foreach (var store in allStores)
        {
            if (ApplyCurrentIngestionVersion(store))
                await stores.UpdateAsync(store, ct);
        }

        foreach (var snapshot in allStores.Where(x => x.Documents.Count > 0 && NeedsIndexing(x)))
        {
            try
            {
                snapshot.Index.State = KnowledgeStoreIndexState.Indexing;
                await stores.UpdateAsync(snapshot, ct);
                var indexed = snapshot.Configuration.Clone();
                var generator = await embeddingGeneratorResolver.ResolveAsync(new ServerModel(indexed.ServerId, indexed.Model), ct);
                logger.LogInformation("Indexing Knowledge Store {StoreId} with server {ServerId}, model {ModelName}, and index {IndexType}", snapshot.Id, indexed.ServerId, indexed.Model, knowledgeIndex.GetType().Name);
                var rebuildAll = snapshot.Index.ForceRebuild || snapshot.Index.IndexedConfiguration is null || !snapshot.Configuration.Equals(snapshot.Index.IndexedConfiguration);
                var documentsToIndex = SelectDocumentsToIndex(snapshot, rebuildAll);
                foreach (var document in documentsToIndex)
                {
                    var content = await payloads.ReadCanonicalMarkdownAsync(snapshot.Id, document.Id, ct) ?? throw new InvalidOperationException($"Document '{document.FileName}' canonical Markdown is missing.");
                    var chunks = chunker.Chunk(document.FileName, content, indexed.MaxTokensPerChunk, indexed.OverlapTokens);
                    if (chunks.Count == 0)
                        continue;
                    logger.LogInformation("Indexing {ChunkCount} chunks for Knowledge Store {StoreId}", chunks.Count, snapshot.Id);
                    progress.Begin(snapshot.Id, document.Id, chunks.Count);
                    var first = (await generator.GenerateAsync([chunks[0].Content], cancellationToken: ct))[0].Vector;
                    indexed.Dimensions = first.Length;
                    if (indexed.Dimensions == 0)
                        throw new InvalidOperationException($"Embedding provider returned an empty embedding for Knowledge Store '{snapshot.Name}'.");
                    logger.LogInformation("Knowledge Store {StoreId} embeddings have dimension {EmbeddingDimension}", snapshot.Id, indexed.Dimensions);
                    var records = new List<KnowledgeIndexedChunk>();
                    for (var i = 0; i < chunks.Count; i++)
                    {
                        var embedding = i == 0 ? first : (await generator.GenerateAsync([chunks[i].Content], cancellationToken: ct))[0].Vector;
                        if (embedding.Length != indexed.Dimensions)
                            throw new InvalidOperationException($"Embedding provider returned inconsistent dimensions for Knowledge Store '{snapshot.Name}'. Expected {indexed.Dimensions}, received {embedding.Length}.");
                        records.Add(new KnowledgeIndexedChunk { Id = $"{snapshot.Id:N}:{document.Id:N}:{i}", KnowledgeStoreId = snapshot.Id, DocumentId = document.Id, FileName = chunks[i].FileName, ChunkIndex = chunks[i].ChunkIndex, Content = chunks[i].Content, Section = chunks[i].Section, Embedding = embedding });
                        progress.Report(snapshot.Id, document.Id, i + 1);
                    }
                    await knowledgeIndex.ReplaceDocumentAsync(new KnowledgeDocumentIndexBatch { KnowledgeStoreId = snapshot.Id, DocumentId = document.Id, EmbeddingDimension = indexed.Dimensions, Chunks = records }, ct);
                    var currentStore = await stores.GetAsync(snapshot.Id, ct);
                    var currentDocument = currentStore?.Documents.FirstOrDefault(x => x.Id == document.Id);
                    if (currentDocument is not null && currentStore is not null)
                    {
                        currentDocument.IndexedSourceHash = document.SourceHash;
                        await stores.UpdateAsync(currentStore, ct);
                        progress.Complete(snapshot.Id, document.Id);
                    }
                }
                var current = await stores.GetAsync(snapshot.Id, ct);
                if (current is null)
                    continue;
                if (!current.Configuration.Equals(snapshot.Configuration))
                { current.Index.State = KnowledgeStoreIndexState.Outdated; await stores.UpdateAsync(current, ct); RequestRebuild(); continue; }
                var oldDimension = current.Index.IndexedConfiguration?.Dimensions;
                current.Configuration.Dimensions = indexed.Dimensions;
                current.Index.IndexedConfiguration = indexed;
                current.Index.State = current.Documents.All(d => d.SourceHash == d.IndexedSourceHash) ? KnowledgeStoreIndexState.Ready : KnowledgeStoreIndexState.Outdated;
                current.Index.ForceRebuild = false;
                current.Index.CompletedUtc = DateTime.UtcNow;
                current.Index.LastError = null;
                await stores.UpdateAsync(current, ct);
                if (oldDimension is > 0 && oldDimension != indexed.Dimensions)
                    await knowledgeIndex.DeleteStoreAsync(current.Id, oldDimension.Value, ct);
            }
            catch (Exception ex) { var current = await stores.GetAsync(snapshot.Id, ct); if (current is not null) { current.Index.State = KnowledgeStoreIndexState.Failed; current.Index.LastError = ex.Message; await stores.UpdateAsync(current, ct); } logger.LogError(ex, "Knowledge Store indexing failed for {StoreId}", snapshot.Id); }
            finally { progress.ClearStore(snapshot.Id); }
        }
    }
    internal static bool NeedsIndexing(KnowledgeStore store) =>
        store.Index.State != KnowledgeStoreIndexState.Failed &&
        (store.Index.State is KnowledgeStoreIndexState.NotIndexed or KnowledgeStoreIndexState.Outdated ||
         store.Documents.Any(document => document.SourceHash != document.IndexedSourceHash));

    internal static IReadOnlyList<KnowledgeDocument> SelectDocumentsToIndex(KnowledgeStore store, bool rebuildAll) =>
        rebuildAll ? store.Documents : store.Documents.Where(document => document.SourceHash != document.IndexedSourceHash).ToList();

    internal static bool ApplyCurrentIngestionVersion(KnowledgeStore store)
    {
        if (store.Configuration.IngestionVersion == KnowledgeStoreIndexConfiguration.CurrentIngestionVersion)
            return false;

        store.Configuration.IngestionVersion = KnowledgeStoreIndexConfiguration.CurrentIngestionVersion;
        if (store.Index.State != KnowledgeStoreIndexState.Failed)
            store.Index.State = KnowledgeStoreIndexState.Outdated;
        return true;
    }

}
