using ChatClient.Api.AgentWorkflows.Compatibility;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;

namespace ChatClient.Api.AgentWorkflows;

public sealed class NormalizingWorkflowParticipantResolver(
    ILegacyWorkflowDefinitionNormalizer workflowDefinitionNormalizer,
    WorkflowParticipantResolver innerResolver) : IWorkflowParticipantResolver
{
    public async Task<IReadOnlyList<ResolvedWorkflowParticipant>> ResolveAsync(
        IOrchestrationWorkflowDefinition workflow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var normalizedWorkflow = await workflowDefinitionNormalizer.NormalizeAsync(
            workflow,
            cancellationToken);

        return await innerResolver.ResolveAsync(normalizedWorkflow, cancellationToken);
    }
}
