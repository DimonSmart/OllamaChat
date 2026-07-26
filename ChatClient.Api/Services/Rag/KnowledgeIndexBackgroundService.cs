using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.ML.Tokenizers;

namespace ChatClient.Api.Services.Rag;

public sealed class KnowledgeIndexBackgroundService(IServiceScopeFactory scopes, ILogger<KnowledgeIndexBackgroundService> logger) : BackgroundService, IKnowledgeIndexBackgroundService
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    public void RequestRebuild() { if (_signal.CurrentCount == 0) _signal.Release(); }
    public async Task DeleteStoreVectorsAsync(Guid id, int dimension, CancellationToken ct = default) { if (dimension > 0) { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<KnowledgeVectorStore>().DeleteStoreAsync(id, dimension, ct); } }
    public async Task DeleteDocumentVectorsAsync(Guid id, Guid documentId, CancellationToken ct = default) { using var scope = scopes.CreateScope(); var store = await scope.ServiceProvider.GetRequiredService<IKnowledgeStoreService>().GetAsync(id, ct); var dimension = store?.Index.IndexedConfiguration?.Dimensions ?? 0; if (dimension > 0) await scope.ServiceProvider.GetRequiredService<KnowledgeVectorStore>().DeleteDocumentAsync(id, documentId, dimension, ct); }
    protected override async Task ExecuteAsync(CancellationToken ct) { RequestRebuild(); while (!ct.IsCancellationRequested) { await _signal.WaitAsync(ct); await RebuildAsync(ct); } }
    private async Task RebuildAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var stores = scope.ServiceProvider.GetRequiredService<IKnowledgeStoreService>();
        var payloads = scope.ServiceProvider.GetRequiredService<IKnowledgeDocumentStorage>();
        var ingestion = scope.ServiceProvider.GetRequiredService<IKnowledgeDocumentIngestionService>();
        var ollama = scope.ServiceProvider.GetRequiredService<IOllamaClientService>();
        var vectors = scope.ServiceProvider.GetRequiredService<KnowledgeVectorStore>();
        var allStores = await stores.GetAllAsync(ct);
        foreach (var store in allStores.Where(store => store.Configuration.IngestionVersion != KnowledgeStoreIndexConfiguration.CurrentIngestionVersion))
        {
            store.Configuration.IngestionVersion = KnowledgeStoreIndexConfiguration.CurrentIngestionVersion;
            store.Index.State = KnowledgeStoreIndexState.Outdated;
            await stores.UpdateAsync(store, ct);
        }

        foreach (var snapshot in allStores.Where(x => x.Documents.Count > 0 && NeedsIndexing(x)))
        {
            try
            {
                snapshot.Index.State = KnowledgeStoreIndexState.Indexing;
                await stores.UpdateAsync(snapshot, ct);
                var indexed = snapshot.Configuration.Clone();
                var rebuildAll = snapshot.Index.IndexedConfiguration is null || !snapshot.Configuration.Equals(snapshot.Index.IndexedConfiguration);
                var documentsToIndex = SelectDocumentsToIndex(snapshot, rebuildAll);
                foreach (var document in documentsToIndex)
                {
                    var content = await payloads.ReadCanonicalMarkdownAsync(snapshot.Id, document.Id, ct) ?? throw new InvalidOperationException($"Document '{document.FileName}' canonical Markdown is missing.");
                    await using var markdown = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content), writable: false);
                    var source = await ingestion.ReadCanonicalMarkdownAsync(document.FileName, markdown, ct);
                    var chunks = await ChunkAsync(document.FileName, source, indexed.MaxTokensPerChunk, indexed.OverlapTokens, ct);
                    if (chunks.Count == 0)
                        continue;
                    var first = await ollama.GenerateEmbeddingAsync(chunks[0].Content, new ServerModel(indexed.ServerId, indexed.Model), ct);
                    indexed.Dimensions = first.Length;
                    var records = new List<KnowledgeChunkRecord>();
                    for (var i = 0; i < chunks.Count; i++)
                    { var embedding = i == 0 ? first : await ollama.GenerateEmbeddingAsync(chunks[i].Content, new ServerModel(indexed.ServerId, indexed.Model), ct); records.Add(chunks[i] with { Id = $"{snapshot.Id:N}:{document.Id:N}:{i}", KnowledgeStoreId = snapshot.Id.ToString("N"), DocumentId = document.Id.ToString("N"), Embedding = embedding }); }
                    await vectors.ReplaceDocumentAsync(snapshot.Id, document.Id, indexed.Dimensions, records, ct);
                    var currentStore = await stores.GetAsync(snapshot.Id, ct);
                    var currentDocument = currentStore?.Documents.FirstOrDefault(x => x.Id == document.Id);
                    if (currentDocument is not null && currentStore is not null)
                    {
                        currentDocument.IndexedSourceHash = document.SourceHash;
                        await stores.UpdateAsync(currentStore, ct);
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
                current.Index.CompletedUtc = DateTime.UtcNow;
                current.Index.LastError = null;
                await stores.UpdateAsync(current, ct);
                if (oldDimension is > 0 && oldDimension != indexed.Dimensions)
                    await vectors.DeleteStoreAsync(current.Id, oldDimension.Value, ct);
            }
            catch (Exception ex) { var current = await stores.GetAsync(snapshot.Id, ct); if (current is not null) { current.Index.State = KnowledgeStoreIndexState.Failed; current.Index.LastError = ex.Message; await stores.UpdateAsync(current, ct); } logger.LogError(ex, "Knowledge Store indexing failed for {StoreId}", snapshot.Id); }
        }
    }
    private static bool NeedsIndexing(KnowledgeStore store) =>
        store.Index.State is KnowledgeStoreIndexState.NotIndexed or KnowledgeStoreIndexState.Outdated or KnowledgeStoreIndexState.Failed ||
        store.Documents.Any(document => document.SourceHash != document.IndexedSourceHash);

    internal static IReadOnlyList<KnowledgeDocument> SelectDocumentsToIndex(KnowledgeStore store, bool rebuildAll) =>
        rebuildAll ? store.Documents : store.Documents.Where(document => document.SourceHash != document.IndexedSourceHash).ToList();

    private static async Task<List<KnowledgeChunkRecord>> ChunkAsync(string fileName, IngestionDocument source, int max, int overlap, CancellationToken ct)
    {
        var chunker = new HeaderChunker(new IngestionChunkerOptions(TiktokenTokenizer.CreateForEncoding("cl100k_base", null, null)) { MaxTokensPerChunk = max, OverlapTokens = overlap });
        var result = new List<KnowledgeChunkRecord>();
        await foreach (var chunk in chunker.ProcessAsync(source, ct))
            result.Add(new KnowledgeChunkRecord { FileName = fileName, ChunkIndex = result.Count, Content = chunk.Content, Section = string.IsNullOrWhiteSpace(chunk.Context) ? null : chunk.Context });
        return result;
    }
}
