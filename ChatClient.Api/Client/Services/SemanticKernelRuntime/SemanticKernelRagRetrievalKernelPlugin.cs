using System.ComponentModel;
using ChatClient.Application.Services.Agentic;
using Microsoft.SemanticKernel;

namespace ChatClient.Api.Client.Services.SemanticKernelRuntime;

public sealed class SemanticKernelRagRetrievalKernelPlugin(
    Guid agentId,
    Guid? serverId,
    IAgenticRagContextService ragContextService,
    ILogger<SemanticKernelRagRetrievalKernelPlugin> logger)
{
    [KernelFunction]
    [Description("Retrieve indexed context for the current agent. Use this when you need factual grounding from uploaded documents.")]
    public async Task<string> RetrieveContextAsync(
        [Description("User query used for semantic retrieval from indexed files.")] string query,
        CancellationToken cancellationToken = default)
    {
        var context = await ragContextService.TryBuildContextAsync(agentId, query, serverId, cancellationToken);
        if (!context.HasContext)
        {
            logger.LogDebug("Agentic RAG retrieval returned no context for agent {AgentId}", agentId);
            return "No retrieved context found.";
        }

        return context.ContextText;
    }
}
