using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Services;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.AgentRuntime;

public sealed class AgentInputDefinitionProvider(
    IWorkflowDefinitionService workflowDefinitionService,
    IWorkflowDefinitionCompiler workflowDefinitionCompiler) : IAgentInputDefinitionProvider
{
    public async Task<IReadOnlyList<AgentInputDefinition>> GetInputsAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (reference.Kind == AgentDefinitionKind.SavedAgent)
        {
            return [];
        }

        if (!Guid.TryParse(reference.Id, out var workflowId))
        {
            throw new KeyNotFoundException($"Workflow id '{reference.Id}' is not a valid saved-workflow id.");
        }

        var workflow = await workflowDefinitionService.GetByIdAsync(workflowId);
        if (workflow is null)
        {
            throw new KeyNotFoundException($"Saved workflow '{reference.Id}' was not found.");
        }

        var compiled = await workflowDefinitionCompiler.CompileAsync(
            workflow.SourceCode,
            cancellationToken);
        var definition = compiled.Workflow
            ?? throw new InvalidOperationException("Workflow compilation did not return a workflow definition.");

        return definition.StartInputs
            .Select(static input => new AgentInputDefinition
            {
                Key = input.Key,
                DisplayName = input.DisplayName,
                Description = input.Description,
                Kind = MapKind(input.Kind),
                IsRequired = input.IsRequired,
                Placeholder = input.Placeholder,
                DefaultValue = input.DefaultValue
            })
            .ToList();
    }

    private static AgentInputDefinitionKind MapKind(WorkflowStartInputKind kind) =>
        kind switch
        {
            WorkflowStartInputKind.Text => AgentInputDefinitionKind.Text,
            WorkflowStartInputKind.Number => AgentInputDefinitionKind.Number,
            WorkflowStartInputKind.Boolean => AgentInputDefinitionKind.Boolean,
            WorkflowStartInputKind.Json => AgentInputDefinitionKind.Json,
            WorkflowStartInputKind.MarkdownDocument => AgentInputDefinitionKind.MarkdownDocument,
            _ => AgentInputDefinitionKind.Text
        };
}

public sealed class WorkflowModelRequirementAnalyzer(
    IWorkflowDefinitionService workflowDefinitionService,
    IWorkflowDefinitionCompiler workflowDefinitionCompiler,
    IAgentTemplateService agentTemplateService) : IAgentDefinitionModelRequirementAnalyzer
{
    public Task<AgentModelRequirement> AnalyzeAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken = default) =>
        AnalyzeAsync(reference, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cancellationToken);

    private async Task<AgentModelRequirement> AnalyzeAsync(
        AgentDefinitionReference reference,
        IReadOnlySet<string> workflowPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (reference.Kind == AgentDefinitionKind.SavedAgent)
        {
            return AgentModelRequirement.Required;
        }

        if (!Guid.TryParse(reference.Id, out var workflowId))
        {
            throw new KeyNotFoundException($"Workflow id '{reference.Id}' is not a valid saved-workflow id.");
        }

        var pathKey = workflowId.ToString("D");
        if (workflowPath.Contains(pathKey))
        {
            return AgentModelRequirement.Required;
        }

        var workflow = await workflowDefinitionService.GetByIdAsync(workflowId)
            ?? throw new KeyNotFoundException($"Saved workflow '{reference.Id}' was not found.");
        var compiled = await workflowDefinitionCompiler.CompileAsync(workflow.SourceCode, cancellationToken);
        var definition = compiled.Workflow
            ?? throw new InvalidOperationException("Workflow compilation did not return a workflow definition.");
        var nextPath = workflowPath.Append(pathKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sawLlmParticipant = false;
        var requiresExternalModel = false;

        foreach (var participant in definition.Participants)
        {
            var participantRequirement = await AnalyzeParticipantAsync(
                participant,
                nextPath,
                cancellationToken);
            if (participantRequirement == AgentModelRequirement.None)
            {
                continue;
            }

            sawLlmParticipant = true;
            if (participantRequirement == AgentModelRequirement.Required)
            {
                requiresExternalModel = true;
            }
        }

        if (!sawLlmParticipant)
        {
            return AgentModelRequirement.None;
        }

        return requiresExternalModel
            ? AgentModelRequirement.Required
            : AgentModelRequirement.Optional;
    }

    private async Task<AgentModelRequirement> AnalyzeParticipantAsync(
        WorkflowParticipantDefinition participant,
        IReadOnlySet<string> workflowPath,
        CancellationToken cancellationToken)
    {
        var source = await ResolveSourceAsync(participant, cancellationToken);
        return source switch
        {
            InlineAgentParticipantSource inline => HasConfiguredModel(inline.Agent)
                ? AgentModelRequirement.Optional
                : AgentModelRequirement.Required,
            SavedDefinitionParticipantSource saved when saved.Reference.Kind == AgentDefinitionKind.SavedAgent =>
                await AnalyzeSavedAgentAsync(saved.Reference),
            SavedDefinitionParticipantSource saved => await AnalyzeAsync(
                saved.Reference,
                workflowPath,
                cancellationToken),
            _ => AgentModelRequirement.Required
        };
    }

    private async Task<WorkflowParticipantSource?> ResolveSourceAsync(
        WorkflowParticipantDefinition participant,
        CancellationToken cancellationToken)
    {
        if (participant.Source is not null)
        {
            return participant.Source;
        }

#pragma warning disable CS0618
        if (participant.AgentDraft is not null)
        {
            return new InlineAgentParticipantSource(participant.AgentDraft);
        }

        if (participant.SavedAgentTemplate is not null)
        {
            var savedAgents = await agentTemplateService.GetAllAsync();
            var savedAgent = savedAgents.SingleOrDefault(agent => string.Equals(
                agent.AgentName,
                participant.SavedAgentTemplate.SavedAgentName,
                StringComparison.OrdinalIgnoreCase));
            return savedAgent is null
                ? null
                : new SavedDefinitionParticipantSource(new AgentDefinitionReference(
                    AgentDefinitionKind.SavedAgent,
                    savedAgent.Id.ToString("D")));
        }
#pragma warning restore CS0618

        return null;
    }

    private async Task<AgentModelRequirement> AnalyzeSavedAgentAsync(
        AgentDefinitionReference reference)
    {
        if (!Guid.TryParse(reference.Id, out var agentId))
        {
            return AgentModelRequirement.Required;
        }

        var agent = await agentTemplateService.GetByIdAsync(agentId);
        return agent is not null && HasConfiguredModel(agent)
            ? AgentModelRequirement.Optional
            : AgentModelRequirement.Required;
    }

    private static bool HasConfiguredModel(AgentTemplateDefinition agent) =>
        agent.LlmId is { } id &&
        id != Guid.Empty &&
        !string.IsNullOrWhiteSpace(agent.ModelName);
}
