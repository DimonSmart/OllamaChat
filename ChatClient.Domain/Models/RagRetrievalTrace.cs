namespace ChatClient.Domain.Models;

public enum RagRetrievalInvocationMode
{
    OnDemand,
    BeforeInvoke
}

public enum RagRetrievalTraceStatus
{
    Running,
    Succeeded,
    NoResults,
    Failed,
    Canceled
}

public sealed record RagRetrievalTrace
{
    public required Guid Id { get; init; }
    public required RagRetrievalInvocationMode Mode { get; init; }
    public required RagRetrievalTraceStatus Status { get; init; }
    public required string Query { get; init; }
    public string? ProfileName { get; init; }
    public required RagRetrievalStrategy Strategy { get; init; }
    public int MaxResults { get; init; }
    public double? AppliedMinVectorRelevanceScore { get; init; }
    public bool UsesApplicationDefaultThreshold { get; init; }
    public int? MaxRetrievedContextTokens { get; init; }
    public int AdjacentChunkCount { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<RagRetrievedExcerptTrace> Excerpts { get; init; } = [];
}

public sealed record RagRetrievedExcerptTrace
{
    public required Guid KnowledgeStoreId { get; init; }
    public required string KnowledgeStoreName { get; init; }
    public required Guid DocumentId { get; init; }
    public required string FileName { get; init; }
    public string? Section { get; init; }
    public required string Content { get; init; }
    public double Score { get; init; }
    public double? VectorScore { get; init; }
    public int? VectorRank { get; init; }
    public int? TextRank { get; init; }
    public int StartChunkIndex { get; init; }
    public int EndChunkIndex { get; init; }
    public bool IsTruncated { get; init; }
}
