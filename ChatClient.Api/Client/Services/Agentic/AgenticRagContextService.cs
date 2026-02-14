using System.Text;
using ChatClient.Api.Services;
using ChatClient.Application.Helpers;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class AgenticRagContextService(
    IUserSettingsService userSettingsService,
    ILogger<AgenticRagContextService> logger,
    IOllamaClientService ollamaService,
    IRagVectorSearchService ragSearchService,
    IRagFileService ragFileService) : IAgenticRagContextService
{
    public async Task<AgenticRagContextResult> TryBuildContextAsync(
        Guid agentId,
        string query,
        Guid? serverId = null,
        CancellationToken cancellationToken = default)
    {
        if (agentId == Guid.Empty || string.IsNullOrWhiteSpace(query))
        {
            return new AgenticRagContextResult();
        }

        var files = await ragFileService.GetFilesAsync(agentId);
        if (!files.Any(f => f.HasIndex))
        {
            return new AgenticRagContextResult();
        }

        var settings = await userSettingsService.GetSettingsAsync(cancellationToken);
        var embeddingModel = ModelSelectionHelper.GetEffectiveEmbeddingModel(
            settings.Embedding.Model,
            settings.DefaultModel,
            "Agentic RAG search",
            logger);

        var effectiveServerId = serverId ?? embeddingModel.ServerId;
        var searchQuery = query.Trim();

        try
        {
            var embedding = await ollamaService.GenerateEmbeddingAsync(
                searchQuery,
                new ServerModel(effectiveServerId, embeddingModel.ModelName),
                cancellationToken);

            var response = await ragSearchService.SearchAsync(
                agentId,
                new ReadOnlyMemory<float>(embedding),
                5,
                cancellationToken);

            if (response.Results.Count == 0)
            {
                return new AgenticRagContextResult();
            }

            var builder = new StringBuilder();
            builder.AppendLine("Retrieved context:");
            for (int i = 0; i < response.Results.Count; i++)
            {
                var result = response.Results[i];
                builder.AppendLine($"[{i + 1}] {result.FileName}");
                builder.AppendLine(result.Content.Trim());
                builder.AppendLine();
            }

            return new AgenticRagContextResult
            {
                ContextText = builder.ToString().Trim(),
                Sources = response.Results
            };
        }
        catch (Exception ex) when (!ollamaService.EmbeddingsAvailable)
        {
            logger.LogError(ex, "Embedding service unavailable. Agentic RAG retrieval skipped.");
            return new AgenticRagContextResult();
        }
    }
}
