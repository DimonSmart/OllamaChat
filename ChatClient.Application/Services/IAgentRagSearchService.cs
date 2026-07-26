using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IAgentRagSearchService
{
    Task<bool> HasIndexedContentAsync(
        Guid agentId,
        CancellationToken cancellationToken = default);

    Task<RagSearchResponse> SearchAsync(
        Guid agentId,
        string query,
        int maxResults = 5,
        CancellationToken cancellationToken = default);
}
