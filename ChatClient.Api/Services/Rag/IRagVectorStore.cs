namespace ChatClient.Api.Services.Rag;

public interface IRagVectorStore
{
    Task UpsertFileAsync(
        Guid agentId,
        string fileName,
        IReadOnlyList<RagVectorStoreEntry> entries,
        CancellationToken cancellationToken = default);

    Task RemoveFileAsync(
        Guid agentId,
        string fileName,
        CancellationToken cancellationToken = default);

    Task<bool> HasFileAsync(
        Guid agentId,
        string fileName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RagVectorStoreEntry>> ReadAgentEntriesAsync(
        Guid agentId,
        CancellationToken cancellationToken = default);

    Task<RagVectorResumePlan> BeginIndexingAsync(
        Guid agentId,
        string fileName,
        RagVectorBuildMetadata metadata,
        bool forceReset = false,
        CancellationToken cancellationToken = default);

    Task UpsertEntryAsync(
        Guid agentId,
        string fileName,
        RagVectorStoreEntry entry,
        int processedChunks,
        int totalChunks,
        CancellationToken cancellationToken = default);

    Task CompleteIndexingAsync(
        Guid agentId,
        string fileName,
        int totalChunks,
        CancellationToken cancellationToken = default);

    Task MarkIndexingFailedAsync(
        Guid agentId,
        string fileName,
        string error,
        CancellationToken cancellationToken = default);

    Task ClearAllAsync(CancellationToken cancellationToken = default);
}

public sealed record RagVectorStoreEntry(string FileName, int Index, string Text, float[] Vector);

public sealed record RagVectorBuildMetadata(
    string SourceHash,
    DateTime SourceModifiedUtc,
    string EmbeddingModel,
    int LineChunkSize,
    int ParagraphChunkSize,
    int ParagraphOverlap,
    int TotalChunks);

public sealed record RagVectorResumePlan(int StartIndex, bool Rebuilt);
