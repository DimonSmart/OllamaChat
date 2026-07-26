using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.Rag;

public sealed class RagVectorSearchService(RagVectorDataStore vectors, IUserSettingsService settings, ILogger<RagVectorSearchService> logger) : IRagVectorSearchService
{
    public async Task<RagSearchResponse> SearchAsync(Guid agentId, ReadOnlyMemory<float> queryVector, int maxResults = 5, CancellationToken cancellationToken = default)
    {
        var threshold = (await settings.GetSettingsAsync(cancellationToken)).Embedding.RagMinRelevanceScore;
        var response = await vectors.SearchAsync(agentId, queryVector, maxResults, threshold, cancellationToken);
        logger.LogDebug("RAG vector search AgentId={AgentId} TopN={TopN} Results={Results} MinRelevanceScore={MinRelevanceScore}", agentId, maxResults, response.Results.Count, threshold);
        return response;
    }
}
