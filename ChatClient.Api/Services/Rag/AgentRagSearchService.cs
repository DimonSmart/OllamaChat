using ChatClient.Application.Helpers;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.Rag;

public sealed class AgentRagSearchService(
    IUserSettingsService userSettingsService,
    ILogger<AgentRagSearchService> logger,
    IOllamaClientService ollamaService,
    IRagVectorSearchService ragVectorSearchService,
    IRagFileService ragFileService) : IAgentRagSearchService
{
    public async Task<bool> HasIndexedContentAsync(
        Guid agentId,
        CancellationToken cancellationToken = default)
    {
        if (agentId == Guid.Empty)
        {
            return false;
        }

        var files = await ragFileService.GetFilesAsync(agentId);
        return files.Any(static file => file.HasIndex);
    }

    public async Task<RagSearchResponse> SearchAsync(
        Guid agentId,
        string query,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        var searchQuery = query?.Trim();
        if (agentId == Guid.Empty || string.IsNullOrWhiteSpace(searchQuery))
        {
            return new RagSearchResponse();
        }

        if (!await HasIndexedContentAsync(agentId, cancellationToken))
        {
            return new RagSearchResponse();
        }

        var settings = await userSettingsService.GetSettingsAsync(cancellationToken);
        var embeddingModel = ModelSelectionHelper.GetEffectiveEmbeddingModel(
            settings.Embedding.Model,
            settings.DefaultModel,
            "Agent RAG search",
            logger);

        try
        {
            var embedding = await ollamaService.GenerateEmbeddingAsync(
                searchQuery,
                new ServerModel(embeddingModel.ServerId, embeddingModel.ModelName),
                cancellationToken);

            return await ragVectorSearchService.SearchAsync(
                agentId,
                new ReadOnlyMemory<float>(embedding),
                maxResults,
                cancellationToken);
        }
        catch (Exception ex) when (!ollamaService.EmbeddingsAvailable)
        {
            logger.LogError(ex, "Embedding service unavailable. Agent RAG retrieval skipped.");
            return new RagSearchResponse();
        }
    }
}
