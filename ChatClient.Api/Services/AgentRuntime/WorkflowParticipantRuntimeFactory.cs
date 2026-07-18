using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Services.AgentRuntime;

namespace ChatClient.Api.Services.AgentRuntime;

public interface IWorkflowParticipantRuntimeFactory
{
    Task<WorkflowRuntimeParticipant> CreateAsync(
        ResolvedWorkflowParticipant participant,
        AgentRuntimeCreationContext creationContext,
        CancellationToken cancellationToken = default);
}

public sealed class WorkflowParticipantRuntimeFactory : IWorkflowParticipantRuntimeFactory
{
    public Task<WorkflowRuntimeParticipant> CreateAsync(
        ResolvedWorkflowParticipant participant,
        AgentRuntimeCreationContext creationContext,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return participant.Source switch
        {
            MaterializedLlmParticipantSource materialized => Task.FromResult(CreateMaterialized(
                participant,
                materialized,
                creationContext)),
            ReferencedParticipantSource referenced => Task.FromResult(CreateReferenced(
                participant,
                referenced)),
            _ => throw new NotSupportedException(
                $"Workflow participant '{participant.ParticipantId}' has unsupported source '{participant.Source.GetType().Name}'.")
        };
    }

    private WorkflowRuntimeParticipant CreateMaterialized(
        ResolvedWorkflowParticipant participant,
        MaterializedLlmParticipantSource source,
        AgentRuntimeCreationContext creationContext)
    {
        return new WorkflowRuntimeParticipant
        {
            Id = participant.ParticipantId,
            DisplayName = participant.DisplayName,
            Summary = participant.Summary,
            Source = source,
            RuntimeKind = AgentRuntimeKind.LlmAgent
        };
    }

    private static WorkflowRuntimeParticipant CreateReferenced(
        ResolvedWorkflowParticipant participant,
        ReferencedParticipantSource source)
    {
        return new WorkflowRuntimeParticipant
        {
            Id = participant.ParticipantId,
            DisplayName = participant.DisplayName,
            Summary = participant.Summary,
            Source = source,
            RuntimeKind = participant.RuntimeKind
        };
    }
}

public sealed class AgentRunContextFactory : IAgentRunContextFactory
{
    public AgentRunContext CreateRoot(
        AgentDefinitionDescriptor definition,
        string? conversationId = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var stack = new List<AgentRunFrame>
        {
            new()
            {
                Definition = AgentDefinitionReferenceComparer.Instance.Normalize(definition.Reference),
                DisplayName = definition.Name
            }
        };

        if (stack.Count != 1)
        {
            throw new InvalidOperationException("Root run context must contain exactly one definition stack frame.");
        }

        return new AgentRunContext
        {
            RunId = Guid.NewGuid().ToString("N"),
            ConversationId = conversationId,
            DefinitionStack = stack
        };
    }

    public AgentRunContext CreateChild(
        AgentRunContext parent,
        AgentDefinitionDescriptor childDefinition,
        WorkflowParticipantInvocation? invocation = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(childDefinition);

        if (parent.DefinitionStack.Count == 0)
        {
            throw new InvalidOperationException(
                "Cannot create a child run context from an empty definition stack.");
        }

        var parentRunId = parent.RunId;
        if (string.IsNullOrWhiteSpace(parentRunId))
        {
            throw new InvalidOperationException("Parent run context must have a run id.");
        }

        var stack = parent.DefinitionStack.Concat([
            new AgentRunFrame
            {
                Definition = AgentDefinitionReferenceComparer.Instance.Normalize(childDefinition.Reference),
                DisplayName = childDefinition.Name,
                ParticipantId = invocation?.ParticipantId,
                ParticipantDisplayName = invocation?.ParticipantDisplayName
            }
        ]).ToList();

        if (stack.Count != parent.DefinitionStack.Count + 1)
        {
            throw new InvalidOperationException("Child run context must append exactly one definition stack frame.");
        }

        var runId = Guid.NewGuid().ToString("N");
        if (string.Equals(runId, parentRunId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Child run context must have a new run id.");
        }

        return new AgentRunContext
        {
            RunId = runId,
            ParentRunId = parentRunId,
            ConversationId = parent.ConversationId,
            DefinitionStack = stack
        };
    }

}
