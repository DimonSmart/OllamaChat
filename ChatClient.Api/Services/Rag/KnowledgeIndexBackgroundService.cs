using ChatClient.Application.Helpers;
using ChatClient.Application.Repositories;
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
    public async Task DeleteStoreVectorsAsync(Guid id, CancellationToken ct = default) { using var scope = scopes.CreateScope(); var store = await scope.ServiceProvider.GetRequiredService<IKnowledgeStoreService>().GetAsync(id, ct); if (store is not null) await scope.ServiceProvider.GetRequiredService<KnowledgeVectorStore>().DeleteStoreAsync(id, store.Configuration.Dimensions, ct); }
    public async Task DeleteDocumentVectorsAsync(Guid id, Guid documentId, CancellationToken ct = default) { using var scope = scopes.CreateScope(); var store = await scope.ServiceProvider.GetRequiredService<IKnowledgeStoreService>().GetAsync(id, ct); if (store is not null) await scope.ServiceProvider.GetRequiredService<KnowledgeVectorStore>().DeleteDocumentAsync(id, documentId, store.Configuration.Dimensions, ct); }
    protected override async Task ExecuteAsync(CancellationToken ct) { RequestRebuild(); while (!ct.IsCancellationRequested) { await _signal.WaitAsync(ct); await RebuildAsync(ct); } }
    private async Task RebuildAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IKnowledgeStoreRepository>();
        var settings = await scope.ServiceProvider.GetRequiredService<IUserSettingsService>().GetSettingsAsync(ct);
        var ollama = scope.ServiceProvider.GetRequiredService<IOllamaClientService>();
        var vectors = scope.ServiceProvider.GetRequiredService<KnowledgeVectorStore>();
        var model = ModelSelectionHelper.GetEffectiveEmbeddingModel(settings.Embedding.Model, settings.DefaultModel, "Knowledge Store indexing", logger);
        var stores = (await repository.GetAllAsync(ct)).ToList();
        foreach (var store in stores.Where(s => s.Documents.Count > 0 && (s.Index.State is KnowledgeStoreIndexState.NotIndexed or KnowledgeStoreIndexState.Outdated or KnowledgeStoreIndexState.Failed)))
        {
            try
            {
                store.Index.State = KnowledgeStoreIndexState.Indexing;
                foreach (var document in store.Documents)
                {
                    var chunks = await ChunkAsync(document, store.Configuration.MaxTokensPerChunk, store.Configuration.OverlapTokens, ct);
                    var first = await ollama.GenerateEmbeddingAsync(chunks[0].Content, new ServerModel(model.ServerId, model.ModelName), ct);
                    store.Configuration.ServerId = model.ServerId;
                    store.Configuration.Model = model.ModelName;
                    store.Configuration.Dimensions = first.Length;
                    var records = new List<KnowledgeChunkRecord>();
                    for (var i = 0; i < chunks.Count; i++)
                    { var embedding = i == 0 ? first : await ollama.GenerateEmbeddingAsync(chunks[i].Content, new ServerModel(model.ServerId, model.ModelName), ct); records.Add(chunks[i] with { Id = $"{store.Id:N}:{document.Id:N}:{i}", KnowledgeStoreId = store.Id.ToString("N"), DocumentId = document.Id.ToString("N"), Embedding = embedding }); }
                    await vectors.ReplaceDocumentAsync(store, document, records, ct);
                }
                store.Index.IndexedConfiguration = store.Configuration.Clone();
                store.Index.State = KnowledgeStoreIndexState.Ready;
                store.Index.CompletedUtc = DateTime.UtcNow;
                store.Index.LastError = null;
            }
            catch (Exception ex) { store.Index.State = KnowledgeStoreIndexState.Failed; store.Index.LastError = ex.Message; logger.LogError(ex, "Knowledge Store indexing failed for {StoreId}", store.Id); }
            await repository.SaveAllAsync(stores, ct);
        }
    }
    private static async Task<List<KnowledgeChunkRecord>> ChunkAsync(KnowledgeDocument document, int max, int overlap, CancellationToken ct)
    {
        var source = new IngestionDocument(document.FileName);
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph(document.Content));
        source.Sections.Add(section);
        var chunker = new DocumentTokenChunker(new IngestionChunkerOptions(TiktokenTokenizer.CreateForEncoding("cl100k_base", null, null)) { MaxTokensPerChunk = max, OverlapTokens = overlap });
        var result = new List<KnowledgeChunkRecord>();
        await foreach (var chunk in chunker.ProcessAsync(source, ct))
            result.Add(new KnowledgeChunkRecord { FileName = document.FileName, ChunkIndex = result.Count, Content = chunk.Content });
        return result;
    }
}
