using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

namespace ChatClient.Api.Services.Rag;

public sealed class RagVectorDataStore(IConfiguration configuration)
{
    private readonly string _connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = StoragePathResolver.ResolveUserPath(configuration, configuration["RagVectorStore:DatabasePath"], FilePathConstants.DefaultRagVectorDatabaseFile) }.ToString();

    public async Task ReplaceFileAsync(Guid agentId, string fileName, IReadOnlyList<RagChunkRecord> chunks, int dimension, CancellationToken cancellationToken)
    {
        var collection = await GetCollectionAsync(dimension, cancellationToken);
        var agentIdValue = agentId.ToString("N");
        await foreach (var record in collection.GetAsync(x => x.AgentId == agentIdValue && x.FileName == fileName, int.MaxValue, null, cancellationToken))
            await collection.DeleteAsync(record.Id, cancellationToken);
        await collection.UpsertAsync(chunks, cancellationToken);
    }

    public async Task<RagSearchResponse> SearchAsync(Guid agentId, ReadOnlyMemory<float> query, int maxResults, double minRelevanceScore, CancellationToken cancellationToken)
    {
        if (query.IsEmpty)
            return new RagSearchResponse();
        var collection = await GetCollectionAsync(query.Length, cancellationToken);
        var agentIdValue = agentId.ToString("N");
        var options = new VectorSearchOptions<RagChunkRecord>
        {
            Filter = x => x.AgentId == agentIdValue,
            ScoreThreshold = ToMaxCosineDistance(minRelevanceScore),
            IncludeVectors = false
        };
        var results = new List<RagSearchResult>();
        await foreach (var result in collection.SearchAsync(query, maxResults, options, cancellationToken))
            results.Add(new RagSearchResult
            {
                FileName = result.Record.FileName,
                Content = result.Record.Content,
                Score = ToRelevanceScore(result.Score ?? 1d)
            });
        return new RagSearchResponse { Total = results.Count, Results = results };
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        // A process-wide clear is intentionally not supported: collections can
        // contain vectors for independent knowledge stores and dimensions.
        await Task.CompletedTask;
    }

    private async Task<VectorStoreCollection<string, RagChunkRecord>> GetCollectionAsync(int dimension, CancellationToken cancellationToken)
    {
        var store = new SqliteVectorStore(_connectionString);
        var definition = new VectorStoreCollectionDefinition
        {
            Properties =
            [
                new VectorStoreKeyProperty(nameof(RagChunkRecord.Id), typeof(string)),
                new VectorStoreDataProperty(nameof(RagChunkRecord.AgentId), typeof(string)),
                new VectorStoreDataProperty(nameof(RagChunkRecord.FileName), typeof(string)),
                new VectorStoreDataProperty(nameof(RagChunkRecord.ChunkIndex), typeof(int)),
                new VectorStoreDataProperty(nameof(RagChunkRecord.Content), typeof(string)),
                new VectorStoreDataProperty(nameof(RagChunkRecord.Section), typeof(string)),
                new VectorStoreVectorProperty(nameof(RagChunkRecord.Embedding), typeof(ReadOnlyMemory<float>), dimension)
                {
                    DistanceFunction = DistanceFunction.CosineDistance
                }
            ]
        };
        var collection = store.GetCollection<string, RagChunkRecord>($"rag_chunks_{dimension}", definition);
        await collection.EnsureCollectionExistsAsync(cancellationToken);
        return collection;
    }

    internal static double ToMaxCosineDistance(double minRelevanceScore) => 1d - Math.Clamp(minRelevanceScore, -1d, 1d);

    internal static double ToRelevanceScore(double cosineDistance) => 1d - cosineDistance;
}
