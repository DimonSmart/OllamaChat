using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public sealed record KnowledgeDocumentIndexBatch
{
    public required Guid KnowledgeStoreId { get; init; }
    public required Guid DocumentId { get; init; }
    public required int EmbeddingDimension { get; init; }
    public required IReadOnlyList<KnowledgeIndexedChunk> Chunks { get; init; }
}

public sealed record KnowledgeVectorSearchRequest
{
    public required KnowledgeStore Store { get; init; }
    public required ReadOnlyMemory<float> QueryEmbedding { get; init; }
    public required int MaxResults { get; init; }
    public double? MinRelevanceScore { get; init; }
}

public sealed record KnowledgeIndexedChunk
{
    public required string Id { get; init; }
    public required Guid KnowledgeStoreId { get; init; }
    public required Guid DocumentId { get; init; }
    public required string FileName { get; init; }
    public required int ChunkIndex { get; init; }
    public required string Content { get; init; }
    public string? Section { get; init; }
    public required ReadOnlyMemory<float> Embedding { get; init; }
}
