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
                Definition = definition.Reference,
                DisplayName = definition.Name
            }
        };

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

        var stack = parent.DefinitionStack.Concat([
            new AgentRunFrame
            {
                Definition = childDefinition.Reference,
                DisplayName = childDefinition.Name,
                ParticipantId = invocation?.ParticipantId,
                ParticipantDisplayName = invocation?.ParticipantDisplayName
            }
        ]).ToList();

        return new AgentRunContext
        {
            RunId = Guid.NewGuid().ToString("N"),
            ParentRunId = parent.RunId,
            ConversationId = parent.ConversationId,
            DefinitionStack = stack
        };
    }
}
