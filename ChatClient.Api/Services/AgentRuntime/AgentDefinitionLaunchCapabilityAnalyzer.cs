using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Compatibility;
using ChatClient.Application.Services;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.AgentRuntime;

public sealed class AgentDefinitionLaunchCapabilityAnalyzer(
    IAgentTemplateService agentTemplateService,
    IWorkflowDefinitionService workflowDefinitionService,
    IWorkflowDefinitionCompiler workflowDefinitionCompiler,
    ILegacyWorkflowDefinitionNormalizer legacyWorkflowDefinitionNormalizer) : IAgentDefinitionLaunchCapabilityAnalyzer
{
    public Task<AgentLaunchCapabilities> AnalyzeAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken = default) =>
        AnalyzeAsync(reference, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cancellationToken);

    private async Task<AgentLaunchCapabilities> AnalyzeAsync(
        AgentDefinitionReference reference,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!visited.Add(AgentDefinitionReferenceComparer.Instance.GetKey(reference)))
        {
            return new AgentLaunchCapabilities();
        }

        if (reference.Kind == AgentDefinitionKind.SavedAgent)
        {
            if (!Guid.TryParse(reference.Id, out var agentId))
            {
                throw new KeyNotFoundException($"Agent id '{reference.Id}' is not a valid saved-agent id.");
            }

            var agent = await agentTemplateService.GetByIdAsync(agentId)
                ?? throw new KeyNotFoundException($"Saved agent '{reference.Id}' was not found.");
            return await AnalyzeAgentAsync(agent, visited, cancellationToken);
        }

        if (!Guid.TryParse(reference.Id, out var workflowId))
        {
            throw new KeyNotFoundException($"Workflow id '{reference.Id}' is not a valid saved-workflow id.");
        }

        var workflow = await workflowDefinitionService.GetByIdAsync(workflowId)
            ?? throw new KeyNotFoundException($"Saved workflow '{reference.Id}' was not found.");
        var compiled = await workflowDefinitionCompiler.CompileAsync(workflow.SourceCode, cancellationToken);
        var definition = compiled.Workflow
            ?? throw new InvalidOperationException("Workflow compilation did not return a workflow definition.");
        var normalized = await legacyWorkflowDefinitionNormalizer.NormalizeAsync(definition, cancellationToken);

        var supportsWorkspace = false;
        var supportsSandbox = false;
        foreach (var participant in normalized.Participants)
        {
            var participantCapabilities = await AnalyzeParticipantAsync(participant, visited, cancellationToken);
            supportsWorkspace |= participantCapabilities.SupportsWorkspace;
            supportsSandbox |= participantCapabilities.SupportsSandboxProfile;
        }

        return new AgentLaunchCapabilities
        {
            SupportsWorkspace = supportsWorkspace,
            SupportsSandboxProfile = supportsSandbox
        };
    }

    private async Task<AgentLaunchCapabilities> AnalyzeParticipantAsync(
        WorkflowParticipantDefinition participant,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        return participant.Source switch
        {
            InlineAgentParticipantSource inline => AnalyzeAgent(inline.Agent),
            SavedDefinitionParticipantSource saved => await AnalyzeAsync(saved.Reference, visited, cancellationToken),
            _ => new AgentLaunchCapabilities()
        };
    }

    private async Task<AgentLaunchCapabilities> AnalyzeAgentAsync(
        AgentTemplateDefinition agent,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        var capabilities = AnalyzeAgent(agent);

        foreach (var backgroundAgentId in agent.BackgroundAgentIds.Distinct())
        {
            var backgroundCapabilities = await AnalyzeAsync(
                new AgentDefinitionReference(
                    AgentDefinitionKind.SavedAgent,
                    backgroundAgentId.ToString("D")),
                visited,
                cancellationToken);
            capabilities = Merge(capabilities, backgroundCapabilities);
        }

        return capabilities;
    }

    private static AgentLaunchCapabilities AnalyzeAgent(AgentTemplateDefinition agent)
    {
        var supportsSandbox = agent.EnableShell;
        return new AgentLaunchCapabilities
        {
            SupportsWorkspace = supportsSandbox || agent.FileAccessProviderProfileId is not null,
            SupportsSandboxProfile = supportsSandbox
        };
    }

    private static AgentLaunchCapabilities Merge(
        AgentLaunchCapabilities left,
        AgentLaunchCapabilities right) =>
        new()
        {
            SupportsMcpBindingOverrides = left.SupportsMcpBindingOverrides || right.SupportsMcpBindingOverrides,
            SupportsWorkspace = left.SupportsWorkspace || right.SupportsWorkspace,
            SupportsSandboxProfile = left.SupportsSandboxProfile || right.SupportsSandboxProfile
        };
}
