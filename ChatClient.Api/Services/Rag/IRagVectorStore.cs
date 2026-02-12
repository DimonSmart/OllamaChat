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
}

public sealed record RagVectorStoreEntry(string FileName, int Index, string Text, float[] Vector);
