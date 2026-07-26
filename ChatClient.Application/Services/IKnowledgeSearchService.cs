using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IKnowledgeSearchService
{
    Task<bool> HasReadyContentAsync(IReadOnlyCollection<Guid> storeIds, CancellationToken cancellationToken = default);
    Task<RagSearchResponse> SearchAsync(IReadOnlyCollection<Guid> storeIds, string query, int maxResults = 5, CancellationToken cancellationToken = default);
}
