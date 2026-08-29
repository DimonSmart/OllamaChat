using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Api.AgentWorkflows;

public interface IWorkflowDefinitionValidator
{
    void Validate(IOrchestrationWorkflowDefinition workflow);
}

public sealed class WorkflowDefinitionValidator : IWorkflowDefinitionValidator
{
    public void Validate(IOrchestrationWorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ValidateParticipants(workflow);
        ValidateStartInputs(workflow);

        switch (workflow)
        {
            case SequentialWorkflowDefinition sequential:
                ValidateParticipantSet("Sequential workflow", sequential.ParticipantOrder, workflow.Participants);
                break;
            case ConcurrentWorkflowDefinition concurrent:
                ValidateParticipantSet("Concurrent workflow", concurrent.ParticipantIds, workflow.Participants);
                break;
            case GroupChatWorkflowDefinition groupChat:
                ValidateParticipantSet("Group chat workflow", groupChat.ParticipantIds, workflow.Participants);
                GroupChatWorkflowManagerValidator.Validate(groupChat.Manager, groupChat.ParticipantIds);
                break;
            case AgentWorkflowDefinition handoff:
                ValidateHandoff(handoff);
                break;
            default:
                throw new InvalidOperationException($"Unsupported workflow kind '{workflow.Kind}'.");
        }
    }

    private static void ValidateParticipants(IOrchestrationWorkflowDefinition workflow)
    {
        if (workflow.Participants.Count == 0)
        {
            throw new InvalidOperationException("Workflow must define at least one participant.");
        }

        var duplicateId = workflow.Participants.GroupBy(static participant => participant.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1)?.Key;
        if (!string.IsNullOrWhiteSpace(duplicateId))
        {
            throw new InvalidOperationException($"Workflow contains duplicate participant id '{duplicateId}'.");
        }

        foreach (var participant in workflow.Participants)
        {
            if (string.IsNullOrWhiteSpace(participant.Id))
            {
                throw new InvalidOperationException("Workflow participant id is required.");
            }

            if (participant.Source is null)
            {
                throw new InvalidOperationException($"Workflow participant '{participant.Id}' has no executable source.");
            }

            if (participant.Source is SavedDefinitionParticipantSource saved &&
                !Guid.TryParse(saved.Reference.Id, out _))
            {
                throw new InvalidOperationException(
                    $"Workflow participant '{participant.Id}' references a saved definition with an invalid id.");
            }

            if (participant.Source is SavedDefinitionParticipantSource savedWorkflow &&
                savedWorkflow.Reference.Kind == AgentDefinitionKind.SavedWorkflow &&
                participant.Overrides.Llm is not null)
            {
                throw new InvalidOperationException(
                    $"Workflow participant '{participant.Id}' applies LLM overrides to a saved workflow.");
            }
        }
    }

    private static void ValidateStartInputs(IOrchestrationWorkflowDefinition workflow)
    {
        var duplicateKey = workflow.StartInputs.GroupBy(static input => input.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1)?.Key;
        if (!string.IsNullOrWhiteSpace(duplicateKey))
        {
            throw new InvalidOperationException($"Workflow contains duplicate start input key '{duplicateKey}'.");
        }
    }

    private static void ValidateParticipantSet(
        string workflowType,
        IReadOnlyList<string> participantIds,
        IReadOnlyList<WorkflowParticipantDefinition> participants)
    {
        if (participantIds.Count == 0)
        {
            throw new InvalidOperationException($"{workflowType} must define at least one participant.");
        }

        var definedIds = participants.Select(static participant => participant.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingId = participantIds.FirstOrDefault(id => !definedIds.Contains(id));
        if (missingId is not null)
        {
            throw new InvalidOperationException($"{workflowType} participant '{missingId}' is not defined.");
        }

        var repeatedId = participantIds.GroupBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1)?.Key;
        if (!string.IsNullOrWhiteSpace(repeatedId))
        {
            throw new InvalidOperationException($"{workflowType} participant '{repeatedId}' is ordered more than once.");
        }
    }

    private static void ValidateHandoff(AgentWorkflowDefinition workflow)
    {
        var definedIds = workflow.Participants.Select(static participant => participant.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(workflow.StartParticipantId) || !definedIds.Contains(workflow.StartParticipantId))
        {
            throw new InvalidOperationException($"Workflow start agent '{workflow.StartParticipantId}' is not defined.");
        }

        var missing = workflow.Handoffs.FirstOrDefault(handoff =>
            !definedIds.Contains(handoff.FromParticipantId) || !definedIds.Contains(handoff.ToParticipantId));
        if (missing is not null)
        {
            throw new InvalidOperationException(
                $"Workflow handoff '{missing.FromParticipantId} -> {missing.ToParticipantId}' references an undefined agent.");
        }
    }
}
