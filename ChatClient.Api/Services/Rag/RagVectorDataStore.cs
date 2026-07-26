using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

namespace ChatClient.Api.Services.Rag;

public sealed class RagVectorDataStore(IConfiguration configuration)
{
    private const string CollectionName = "rag_chunks";
    private readonly string _connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = StoragePathResolver.ResolveUserPath(configuration, configuration["RagVectorStore:DatabasePath"], FilePathConstants.DefaultRagVectorDatabaseFile) }.ToString();
    private int _dimension;
    private VectorStoreCollection<string, RagChunkRecord>? _collection;

    public async Task ReplaceFileAsync(Guid agentId, string fileName, IReadOnlyList<RagChunkRecord> chunks, int dimension, CancellationToken cancellationToken)
    {
        var collection = await GetCollectionAsync(dimension, cancellationToken);
        await foreach (var record in collection.GetAsync(x => x.AgentId == agentId.ToString("N") && x.FileName == fileName, int.MaxValue, null, cancellationToken))
            await collection.DeleteAsync(record.Id, cancellationToken);
        await collection.UpsertAsync(chunks, cancellationToken);
    }

    public async Task<RagSearchResponse> SearchAsync(Guid agentId, ReadOnlyMemory<float> query, int maxResults, double threshold, CancellationToken cancellationToken)
    {
        if (query.IsEmpty)
            return new RagSearchResponse();
        var collection = await GetCollectionAsync(query.Length, cancellationToken);
        var options = new VectorSearchOptions<RagChunkRecord> { Filter = x => x.AgentId == agentId.ToString("N"), ScoreThreshold = threshold, IncludeVectors = false };
        var results = new List<RagSearchResult>();
        await foreach (var result in collection.SearchAsync(query, maxResults, options, cancellationToken))
            results.Add(new RagSearchResult { FileName = result.Record.FileName, Content = result.Record.Content, Score = result.Score ?? 0 });
        return new RagSearchResponse { Total = results.Count, Results = results };
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        if (_collection is not null)
            await _collection.EnsureCollectionDeletedAsync(cancellationToken);
        _collection = null;
        _dimension = 0;
    }

    private async Task<VectorStoreCollection<string, RagChunkRecord>> GetCollectionAsync(int dimension, CancellationToken cancellationToken)
    {
        if (_collection is not null && _dimension == dimension)
            return _collection;
        var store = new SqliteVectorStore(_connectionString);
        if (_collection is not null)
            await _collection.EnsureCollectionDeletedAsync(cancellationToken);
        var definition = new VectorStoreCollectionDefinition { Properties = [new VectorStoreKeyProperty(nameof(RagChunkRecord.Id), typeof(string)), new VectorStoreDataProperty(nameof(RagChunkRecord.AgentId), typeof(string)) { IsIndexed = true }, new VectorStoreDataProperty(nameof(RagChunkRecord.FileName), typeof(string)) { IsIndexed = true }, new VectorStoreDataProperty(nameof(RagChunkRecord.ChunkIndex), typeof(int)), new VectorStoreDataProperty(nameof(RagChunkRecord.Content), typeof(string)), new VectorStoreDataProperty(nameof(RagChunkRecord.Section), typeof(string)), new VectorStoreVectorProperty(nameof(RagChunkRecord.Embedding), typeof(ReadOnlyMemory<float>), dimension) { DistanceFunction = DistanceFunction.CosineSimilarity }] };
        _collection = store.GetCollection<string, RagChunkRecord>(CollectionName, definition);
        await _collection.EnsureCollectionExistsAsync(cancellationToken);
        _dimension = dimension;
        return _collection;
    }
}
