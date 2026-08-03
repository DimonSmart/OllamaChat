using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IKnowledgeSearchService
{
    Task<bool> HasReadyContentAsync(IReadOnlyCollection<Guid> storeIds, CancellationToken cancellationToken = default);
    Task<RagSearchResponse> SearchAsync(KnowledgeSearchRequest request, CancellationToken cancellationToken = default);
}
