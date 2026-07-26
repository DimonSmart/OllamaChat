using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

namespace ChatClient.Api.Services.Rag;

public sealed class KnowledgeVectorStore(IConfiguration configuration)
{
    private readonly string _connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = StoragePathResolver.ResolveUserPath(configuration, configuration["RagVectorStore:DatabasePath"], FilePathConstants.DefaultRagVectorDatabaseFile) }.ToString();

    public async Task ReplaceDocumentAsync(KnowledgeStore store, KnowledgeDocument document, IReadOnlyList<KnowledgeChunkRecord> chunks, CancellationToken ct)
    {
        var collection = await GetCollectionAsync(store.Configuration.Dimensions, ct);
        await foreach (var item in collection.GetAsync(x => x.KnowledgeStoreId == store.Id.ToString("N") && x.DocumentId == document.Id.ToString("N"), int.MaxValue, null, ct))
            await collection.DeleteAsync(item.Id, ct);
        await collection.UpsertAsync(chunks, ct);
    }

    public async Task<IReadOnlyList<RagSearchResult>> SearchAsync(KnowledgeStore store, ReadOnlyMemory<float> query, int max, double threshold, CancellationToken ct)
    {
        if (store.Configuration.Dimensions != query.Length)
            throw new InvalidOperationException($"Knowledge Store '{store.Name}' index was built with dimension {store.Configuration.Dimensions}, but the query embedding has dimension {query.Length}. Reindex is required.");
        var collection = await GetCollectionAsync(query.Length, ct);
        var results = new List<RagSearchResult>();
        await foreach (var result in collection.SearchAsync(query, max, new VectorSearchOptions<KnowledgeChunkRecord> { Filter = x => x.KnowledgeStoreId == store.Id.ToString("N"), ScoreThreshold = 1d - Math.Clamp(threshold, -1d, 1d), IncludeVectors = false }, ct))
            results.Add(new RagSearchResult { FileName = result.Record.FileName, Content = result.Record.Content, Score = 1d - (result.Score ?? 1d) });
        return results;
    }

    public async Task DeleteStoreAsync(Guid storeId, int dimension, CancellationToken ct)
    {
        if (dimension <= 0)
            return;
        var id = storeId.ToString("N");
        var collection = await GetCollectionAsync(dimension, ct);
        await foreach (var item in collection.GetAsync(x => x.KnowledgeStoreId == id, int.MaxValue, null, ct))
            await collection.DeleteAsync(item.Id, ct);
    }
    public async Task DeleteDocumentAsync(Guid storeId, Guid documentId, int dimension, CancellationToken ct)
    {
        if (dimension <= 0)
            return;
        var store = storeId.ToString("N");
        var document = documentId.ToString("N");
        var collection = await GetCollectionAsync(dimension, ct);
        await foreach (var item in collection.GetAsync(x => x.KnowledgeStoreId == store && x.DocumentId == document, int.MaxValue, null, ct))
            await collection.DeleteAsync(item.Id, ct);
    }
    private async Task<VectorStoreCollection<string, KnowledgeChunkRecord>> GetCollectionAsync(int dimension, CancellationToken ct)
    {
        if (dimension <= 0)
            throw new InvalidOperationException("Knowledge Store has no indexed embedding dimension.");
        var definition = new VectorStoreCollectionDefinition { Properties = [new VectorStoreKeyProperty(nameof(KnowledgeChunkRecord.Id), typeof(string)), new VectorStoreDataProperty(nameof(KnowledgeChunkRecord.KnowledgeStoreId), typeof(string)), new VectorStoreDataProperty(nameof(KnowledgeChunkRecord.DocumentId), typeof(string)), new VectorStoreDataProperty(nameof(KnowledgeChunkRecord.FileName), typeof(string)), new VectorStoreDataProperty(nameof(KnowledgeChunkRecord.ChunkIndex), typeof(int)), new VectorStoreDataProperty(nameof(KnowledgeChunkRecord.Content), typeof(string)), new VectorStoreDataProperty(nameof(KnowledgeChunkRecord.Section), typeof(string)), new VectorStoreVectorProperty(nameof(KnowledgeChunkRecord.Embedding), typeof(ReadOnlyMemory<float>), dimension) { DistanceFunction = DistanceFunction.CosineDistance }] };
        var collection = new SqliteVectorStore(_connectionString).GetCollection<string, KnowledgeChunkRecord>($"knowledge_chunks_{dimension}", definition);
        await collection.EnsureCollectionExistsAsync(ct);
        return collection;
    }
}
