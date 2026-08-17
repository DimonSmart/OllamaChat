using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using CommunityToolkit.VectorData.SqliteVec;
using Microsoft.Extensions.VectorData;
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

    public async Task ReplaceDocumentAsync(KnowledgeDocumentIndexBatch batch, CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync(async () =>
        {
            var collection = await GetOrCreateCollectionForWriteAsync(batch.EmbeddingDimension, cancellationToken);
            var store = batch.KnowledgeStoreId.ToString("N");
            var document = batch.DocumentId.ToString("N");
            var existingIds = await GetIdsAsync(collection, x => x.KnowledgeStoreId == store && x.DocumentId == document, cancellationToken);
            foreach (var id in existingIds)
                await collection.DeleteAsync(id, cancellationToken);
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
            await collection.UpsertAsync(records, cancellationToken);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<RagSearchResult>> SearchVectorAsync(KnowledgeVectorSearchRequest request, CancellationToken cancellationToken = default)
    {
        var store = request.Store;
        var query = request.QueryEmbedding;
        var dimension = store.Index.IndexedConfiguration?.Dimensions ?? 0;
        if (dimension != query.Length)
            throw new InvalidOperationException($"Knowledge Store '{store.Name}' was indexed with dimension {dimension}, but the current embedding provider returned dimension {query.Length}. Reindex the Knowledge Store.");
        var collection = await GetExistingCollectionForReadAsync(dimension, cancellationToken);
        var storeId = store.Id.ToString("N");
        var results = new List<RagSearchResult>();
        var options = new VectorSearchOptions<SqliteKnowledgeChunkRecord>
        {
            Filter = x => x.KnowledgeStoreId == storeId,
            IncludeVectors = false
        };
        if (request.MinRelevanceScore is double threshold)
            options.ScoreThreshold = 1d - Math.Clamp(threshold, -1d, 1d);
        await foreach (var result in collection.SearchAsync(query, request.MaxResults, options, cancellationToken))
            results.Add(new RagSearchResult { FileName = result.Record.FileName, Section = result.Record.Section, Content = result.Record.Content, Score = 1d - (result.Score ?? 1d) });
        return results;
    }

    public async Task DeleteStoreAsync(Guid storeId, int dimension, CancellationToken cancellationToken = default)
    {
        if (dimension <= 0)
            return;
        await ExecuteWriteAsync(async () =>
        {
            var id = storeId.ToString("N");
            var collection = await GetOrCreateCollectionForWriteAsync(dimension, cancellationToken);
            var existingIds = await GetIdsAsync(collection, x => x.KnowledgeStoreId == id, cancellationToken);
            foreach (var existingId in existingIds)
                await collection.DeleteAsync(existingId, cancellationToken);
        }, cancellationToken);
    }
    public async Task DeleteDocumentAsync(Guid storeId, Guid documentId, int dimension, CancellationToken cancellationToken = default)
    {
        if (dimension <= 0)
            return;
        await ExecuteWriteAsync(async () =>
        {
            var store = storeId.ToString("N");
            var document = documentId.ToString("N");
            var collection = await GetOrCreateCollectionForWriteAsync(dimension, cancellationToken);
            var existingIds = await GetIdsAsync(collection, x => x.KnowledgeStoreId == store && x.DocumentId == document, cancellationToken);
            foreach (var id in existingIds)
                await collection.DeleteAsync(id, cancellationToken);
        }, cancellationToken);
    }

    private async Task ExecuteWriteAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        await foreach (var item in collection.GetAsync(filter, int.MaxValue, null, cancellationToken))
            ids.Add(item.Id);
        return ids;
    }
    private async Task<VectorStoreCollection<string, SqliteKnowledgeChunkRecord>> GetOrCreateCollectionForWriteAsync(int dimension, CancellationToken cancellationToken)
    {
        if (dimension <= 0)
            throw new InvalidOperationException("Knowledge Store has no indexed embedding dimension.");
        var collection = new SqliteVectorStore(_connectionString).GetCollection<string, SqliteKnowledgeChunkRecord>($"knowledge_chunks_{dimension}", CreateDefinition(dimension));
        await collection.EnsureCollectionExistsAsync(cancellationToken);
        return collection;
    }
    private async Task<VectorStoreCollection<string, SqliteKnowledgeChunkRecord>> GetExistingCollectionForReadAsync(int dimension, CancellationToken cancellationToken)
    {
        if (dimension <= 0)
            throw new InvalidOperationException("Knowledge Store has no indexed embedding dimension.");
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", $"knowledge_chunks_{dimension}");
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
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
