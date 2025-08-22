namespace ChatClient.Shared.Services;

using ChatClient.Shared.Models;

public interface IRagVectorSearchService
{
    Task<IReadOnlyList<RagSearchResult>> SearchAsync(Guid agentId, ReadOnlyMemory<float> queryVector, int maxResults = 5, CancellationToken cancellationToken = default);
}
