using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Api.AgentWorkflows;

public interface IWorkflowParticipantResolver
{
    Task<IReadOnlyList<ResolvedWorkflowParticipant>> ResolveAsync(
        IOrchestrationWorkflowDefinition workflow,
        CancellationToken cancellationToken = default);
}

public sealed class WorkflowParticipantResolver(
    IAgentTemplateService agentDescriptionService,
    IAgentDefinitionCatalog definitionCatalog) : IWorkflowParticipantResolver
{
    private readonly IAgentTemplateService _agentDescriptionService = agentDescriptionService;
    private readonly IAgentDefinitionCatalog _definitionCatalog = definitionCatalog;

    public async Task<IReadOnlyList<ResolvedWorkflowParticipant>> ResolveAsync(
        IOrchestrationWorkflowDefinition workflow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        cancellationToken.ThrowIfCancellationRequested();

        var resolved = new List<ResolvedWorkflowParticipant>();
        foreach (var participant in workflow.Participants)
        {
            resolved.Add(await ResolveParticipantAsync(
                participant,
                cancellationToken));
        }

        ResolveInstructionTemplates(resolved);
        return resolved;
    }

    private async Task<ResolvedWorkflowParticipant> ResolveParticipantAsync(
        WorkflowParticipantDefinition participant,
        CancellationToken cancellationToken)
    {
        return participant.Source switch
        {
            InlineAgentParticipantSource inline => ResolveInline(participant, inline.Agent),
            SavedDefinitionParticipantSource saved => await ResolveSavedAsync(
                participant,
                saved.Reference,
                cancellationToken),
            _ => throw new InvalidOperationException(
                $"Workflow participant '{participant.Id}' has no executable source.")
        };
    }

    private async Task<ResolvedWorkflowParticipant> ResolveSavedAsync(
        WorkflowParticipantDefinition participant,
        AgentDefinitionReference reference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference.Id))
        {
            throw new InvalidOperationException(
                $"Workflow participant '{participant.Id}' references a saved definition without an id.");
        }

        if (reference.Kind == AgentDefinitionKind.SavedAgent)
        {
            var draft = await ResolveSavedAgentDraftAsync(reference, cancellationToken);
            ApplyOverrides(draft, participant.Overrides);
            draft.RuntimeAgentId = participant.Id;
            draft.ShortName = participant.Id;
            return new ResolvedWorkflowParticipant
            {
                ParticipantId = participant.Id,
                DisplayName = ResolveDisplayName(participant, draft.AgentName),
                Role = participant.Role,
                Summary = ResolveSummary(participant, draft.Summary),
                RuntimeKind = AgentRuntimeKind.LlmAgent,
                Source = new MaterializedLlmParticipantSource(draft)
            };
        }

        var catalogItem = await _definitionCatalog.FindAsync(reference, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Saved {reference.Kind} '{reference.Id}' was not found for workflow participant '{participant.Id}'.");

        return new ResolvedWorkflowParticipant
        {
            ParticipantId = participant.Id,
            DisplayName = ResolveDisplayName(participant, catalogItem.Name),
            Role = participant.Role,
            Summary = ResolveSummary(participant, catalogItem.Description),
            RuntimeKind = AgentRuntimeKind.WorkflowAgent,
            Source = new ReferencedParticipantSource(reference)
        };
    }

    private static ResolvedWorkflowParticipant ResolveInline(
        WorkflowParticipantDefinition participant,
        AgentTemplateDefinition agent)
    {
        var draft = agent.Clone();
        ApplyOverrides(draft, participant.Overrides);
        draft.RuntimeAgentId = participant.Id;
        draft.ShortName = participant.Id;

        return new ResolvedWorkflowParticipant
        {
            ParticipantId = participant.Id,
            DisplayName = ResolveDisplayName(participant, draft.AgentName),
            Role = participant.Role,
            Summary = ResolveSummary(participant, draft.Summary),
            RuntimeKind = AgentRuntimeKind.LlmAgent,
            Source = new MaterializedLlmParticipantSource(draft)
        };
    }

    private async Task<AgentTemplateDefinition> ResolveSavedAgentDraftAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(reference.Id, out var agentId))
        {
            throw new InvalidOperationException(
                $"Saved agent reference '{reference.Id}' is not a valid saved-agent id.");
        }

        return (await _agentDescriptionService.GetByIdAsync(agentId))?.Clone()
               ?? throw new InvalidOperationException(
                   $"Saved agent '{reference.Id}' was not found.");
    }

    private static string ResolveDisplayName(
        WorkflowParticipantDefinition participant,
        string fallback)
    {
        if (!string.IsNullOrWhiteSpace(participant.Overrides.DisplayName))
        {
            return participant.Overrides.DisplayName.Trim();
        }

        return string.IsNullOrWhiteSpace(fallback) ? participant.Id : fallback.Trim();
    }

    private static string ResolveSummary(
        WorkflowParticipantDefinition participant,
        string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(participant.Overrides.Summary))
        {
            return participant.Overrides.Summary.Trim();
        }

        if (!string.IsNullOrWhiteSpace(participant.Summary))
        {
            return participant.Summary.Trim();
        }

        return fallback?.Trim() ?? string.Empty;
    }

    private static void ApplyOverrides(
        AgentTemplateDefinition draft,
        WorkflowParticipantOverrides overrides)
    {
        if (!string.IsNullOrWhiteSpace(overrides.DisplayName))
        {
            draft.AgentName = overrides.DisplayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(overrides.Llm?.AvatarText))
        {
            draft.AvatarText = overrides.Llm.AvatarText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(overrides.Llm?.Instructions))
        {
            draft.Content = overrides.Llm.Instructions.Trim();
        }

        if (!string.IsNullOrWhiteSpace(overrides.Llm?.AppendedInstructions))
        {
            draft.Content = AppendInstructions(draft.Content, overrides.Llm.AppendedInstructions);
        }
    }

    private static void ResolveInstructionTemplates(List<ResolvedWorkflowParticipant> participants)
    {
        var agentsById = participants
            .Where(static participant => participant.Source is MaterializedLlmParticipantSource)
            .ToDictionary(
                static participant => participant.ParticipantId,
                static participant => new WorkflowInstructionTemplateParticipant(
                    participant.ParticipantId,
                    participant.DisplayName,
                    participant.Role,
                    ((MaterializedLlmParticipantSource)participant.Source).Agent.AvatarText),
                StringComparer.OrdinalIgnoreCase);

        foreach (var participant in participants)
        {
            if (participant.Source is not MaterializedLlmParticipantSource materialized ||
                string.IsNullOrWhiteSpace(materialized.Agent.Content))
            {
                continue;
            }

            materialized.Agent.Content = WorkflowInstructionTemplateResolver.ResolveAgentReferences(
                materialized.Agent.Content,
                participant.ParticipantId,
                agentsById);
        }
    }

    private static string AppendInstructions(string baseInstructions, string appendedInstructions)
    {
        var normalizedBase = baseInstructions?.Trim() ?? string.Empty;
        var normalizedAppendix = appendedInstructions.Trim();

        if (string.IsNullOrWhiteSpace(normalizedBase))
        {
            return normalizedAppendix;
        }

        return $"{normalizedBase}\n\n{normalizedAppendix}";
    }
}
