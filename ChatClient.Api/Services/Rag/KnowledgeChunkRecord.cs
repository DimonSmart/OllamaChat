namespace ChatClient.Api.Services.Rag;

public sealed record KnowledgeChunkRecord
{
    public string Id { get; init; } = string.Empty;
    public string KnowledgeStoreId { get; init; } = string.Empty;
    public string DocumentId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public int ChunkIndex { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? Section { get; init; }
    public ReadOnlyMemory<float> Embedding { get; init; }
}
