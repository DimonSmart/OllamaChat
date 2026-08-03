namespace ChatClient.Domain.Models;

public sealed record KnowledgeSearchRequest
{
    public required IReadOnlyCollection<Guid> KnowledgeStoreIds { get; init; }
    public required string Query { get; init; }
    public int MaxResults { get; init; } = 5;
    public bool UseApplicationDefaultThreshold { get; init; }
    public double? MinVectorRelevanceScore { get; init; }
    public RagRetrievalStrategy Strategy { get; init; } = RagRetrievalStrategy.Hybrid;
    public int? MaxRetrievedContextTokens { get; init; }
    public int AdjacentChunkCount { get; init; }
}
