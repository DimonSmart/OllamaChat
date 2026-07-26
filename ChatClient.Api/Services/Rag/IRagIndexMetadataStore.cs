namespace ChatClient.Api.Services.Rag;

public interface IRagIndexMetadataStore
{
    Task RemoveFileAsync(Guid agentId, string fileName, CancellationToken cancellationToken = default);
    Task<bool> HasFileAsync(Guid agentId, string fileName, CancellationToken cancellationToken = default);
    Task<RagIndexResumePlan> BeginIndexingAsync(Guid agentId, string fileName, RagIndexBuildMetadata metadata, CancellationToken cancellationToken = default);
    Task ReportProgressAsync(Guid agentId, string fileName, int processedChunks, CancellationToken cancellationToken = default);
    Task CompleteIndexingAsync(Guid agentId, string fileName, int totalChunks, CancellationToken cancellationToken = default);
    Task MarkIndexingFailedAsync(Guid agentId, string fileName, string error, CancellationToken cancellationToken = default);
    Task ClearAllAsync(CancellationToken cancellationToken = default);
}

public sealed record RagIndexBuildMetadata(
    string SourceHash,
    DateTime SourceModifiedUtc,
    string EmbeddingModel,
    int EmbeddingDimension,
    int MaxTokensPerChunk,
    int OverlapTokens,
    string IngestionVersion,
    int TotalChunks);

public sealed record RagIndexResumePlan(int StartIndex, bool Rebuilt);
