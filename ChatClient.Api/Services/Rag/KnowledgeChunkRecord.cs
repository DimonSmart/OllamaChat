using Microsoft.Extensions.VectorData;

namespace ChatClient.Api.Services.Rag;

public sealed record KnowledgeChunkRecord
{
    [VectorStoreKey] public string Id { get; init; } = string.Empty;
    [VectorStoreData] public string KnowledgeStoreId { get; init; } = string.Empty;
    [VectorStoreData] public string DocumentId { get; init; } = string.Empty;
    [VectorStoreData] public string FileName { get; init; } = string.Empty;
    [VectorStoreData] public int ChunkIndex { get; init; }
    [VectorStoreData] public string Content { get; init; } = string.Empty;
    [VectorStoreData] public string? Section { get; init; }
    public ReadOnlyMemory<float> Embedding { get; init; }
}
