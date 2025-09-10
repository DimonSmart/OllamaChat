namespace ChatClient.Application.Services;

using ChatClient.Domain.Models;

public interface IRagVectorSearchService
{
    Task<RagSearchResponse> SearchAsync(Guid agentId, ReadOnlyMemory<float> queryVector, int maxResults = 5, CancellationToken cancellationToken = default);
}
