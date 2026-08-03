using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using System.Linq.Expressions;

namespace ChatClient.Api.Services.Rag;

public sealed class SqliteKnowledgeIndex(IConfiguration configuration) : IKnowledgeIndex
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
    {
        DataSource = StoragePathResolver.ResolveUserPath(configuration, configuration["KnowledgeVectorStore:DatabasePath"], FilePathConstants.DefaultKnowledgeVectorDatabaseFile),
        DefaultTimeout = 60
    }.ToString();

    public async Task ReplaceDocumentAsync(KnowledgeDocumentIndexBatch batch, CancellationToken ct = default)
    {
        await ExecuteWriteAsync(async () =>
        {
            var collection = await GetOrCreateCollectionForWriteAsync(batch.EmbeddingDimension, ct);
            var store = batch.KnowledgeStoreId.ToString("N");
            var document = batch.DocumentId.ToString("N");
            var existingIds = await GetIdsAsync(collection, x => x.KnowledgeStoreId == store && x.DocumentId == document, ct);
            foreach (var id in existingIds)
                await collection.DeleteAsync(id, ct);
            var records = batch.Chunks.Select(chunk => new SqliteKnowledgeChunkRecord
            {
                Id = chunk.Id,
                KnowledgeStoreId = chunk.KnowledgeStoreId.ToString("N"),
                DocumentId = chunk.DocumentId.ToString("N"),
                FileName = chunk.FileName,
                ChunkIndex = chunk.ChunkIndex,
                Content = chunk.Content,
                Section = chunk.Section,
                Embedding = chunk.Embedding
            }).ToList();
            await collection.UpsertAsync(records, ct);
        }, ct);
    }

    public async Task<IReadOnlyList<RagSearchResult>> SearchVectorAsync(KnowledgeVectorSearchRequest request, CancellationToken ct = default)
    {
        var store = request.Store;
        var query = request.QueryEmbedding;
        var dimension = store.Index.IndexedConfiguration?.Dimensions ?? 0;
        if (dimension != query.Length)
            throw new InvalidOperationException($"Knowledge Store '{store.Name}' was indexed with dimension {dimension}, but the current embedding provider returned dimension {query.Length}. Reindex the Knowledge Store.");
        var collection = await GetExistingCollectionForReadAsync(dimension, ct);
        var storeId = store.Id.ToString("N");
        var results = new List<RagSearchResult>();
        var options = new VectorSearchOptions<SqliteKnowledgeChunkRecord>
        {
            Filter = x => x.KnowledgeStoreId == storeId,
            IncludeVectors = false
        };
        if (request.MinRelevanceScore is double threshold)
            options.ScoreThreshold = 1d - Math.Clamp(threshold, -1d, 1d);
        await foreach (var result in collection.SearchAsync(query, request.MaxResults, options, ct))
            results.Add(new RagSearchResult { FileName = result.Record.FileName, Section = result.Record.Section, Content = result.Record.Content, Score = 1d - (result.Score ?? 1d) });
        return results;
    }

    public async Task DeleteStoreAsync(Guid storeId, int dimension, CancellationToken ct = default)
    {
        if (dimension <= 0)
            return;
        await ExecuteWriteAsync(async () =>
        {
            var id = storeId.ToString("N");
            var collection = await GetOrCreateCollectionForWriteAsync(dimension, ct);
            var existingIds = await GetIdsAsync(collection, x => x.KnowledgeStoreId == id, ct);
            foreach (var existingId in existingIds)
                await collection.DeleteAsync(existingId, ct);
        }, ct);
    }
    public async Task DeleteDocumentAsync(Guid storeId, Guid documentId, int dimension, CancellationToken ct = default)
    {
        if (dimension <= 0)
            return;
        await ExecuteWriteAsync(async () =>
        {
            var store = storeId.ToString("N");
            var document = documentId.ToString("N");
            var collection = await GetOrCreateCollectionForWriteAsync(dimension, ct);
            var existingIds = await GetIdsAsync(collection, x => x.KnowledgeStoreId == store && x.DocumentId == document, ct);
            foreach (var id in existingIds)
                await collection.DeleteAsync(id, ct);
        }, ct);
    }

    private async Task ExecuteWriteAsync(Func<Task> operation, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await operation();
        }
        finally
        {
            _writeLock.Release();
        }
    }
    private static async Task<List<string>> GetIdsAsync(
        VectorStoreCollection<string, SqliteKnowledgeChunkRecord> collection,
        Expression<Func<SqliteKnowledgeChunkRecord, bool>> filter,
        CancellationToken ct)
    {
        var ids = new List<string>();
        await foreach (var item in collection.GetAsync(filter, int.MaxValue, null, ct))
            ids.Add(item.Id);
        return ids;
    }
    private async Task<VectorStoreCollection<string, SqliteKnowledgeChunkRecord>> GetOrCreateCollectionForWriteAsync(int dimension, CancellationToken ct)
    {
        if (dimension <= 0)
            throw new InvalidOperationException("Knowledge Store has no indexed embedding dimension.");
        var collection = new SqliteVectorStore(_connectionString).GetCollection<string, SqliteKnowledgeChunkRecord>($"knowledge_chunks_{dimension}", CreateDefinition(dimension));
        await collection.EnsureCollectionExistsAsync(ct);
        return collection;
    }
    private async Task<VectorStoreCollection<string, SqliteKnowledgeChunkRecord>> GetExistingCollectionForReadAsync(int dimension, CancellationToken ct)
    {
        if (dimension <= 0)
            throw new InvalidOperationException("Knowledge Store has no indexed embedding dimension.");
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", $"knowledge_chunks_{dimension}");
        if (await command.ExecuteScalarAsync(ct) is null)
            throw new InvalidOperationException($"Knowledge index collection for dimension {dimension} is missing. Reindex the Knowledge Store.");
        return CreateCollection(dimension);
    }
    private VectorStoreCollection<string, SqliteKnowledgeChunkRecord> CreateCollection(int dimension)
    {
        return new SqliteVectorStore(_connectionString).GetCollection<string, SqliteKnowledgeChunkRecord>($"knowledge_chunks_{dimension}", CreateDefinition(dimension));
    }

    private static VectorStoreCollectionDefinition CreateDefinition(int dimension) => new()
    {
        Properties = [new VectorStoreKeyProperty(nameof(SqliteKnowledgeChunkRecord.Id), typeof(string)), new VectorStoreDataProperty(nameof(SqliteKnowledgeChunkRecord.KnowledgeStoreId), typeof(string)), new VectorStoreDataProperty(nameof(SqliteKnowledgeChunkRecord.DocumentId), typeof(string)), new VectorStoreDataProperty(nameof(SqliteKnowledgeChunkRecord.FileName), typeof(string)), new VectorStoreDataProperty(nameof(SqliteKnowledgeChunkRecord.ChunkIndex), typeof(int)), new VectorStoreDataProperty(nameof(SqliteKnowledgeChunkRecord.Content), typeof(string)), new VectorStoreDataProperty(nameof(SqliteKnowledgeChunkRecord.Section), typeof(string)), new VectorStoreVectorProperty(nameof(SqliteKnowledgeChunkRecord.Embedding), typeof(ReadOnlyMemory<float>), dimension) { DistanceFunction = DistanceFunction.CosineDistance }]
    };
}
