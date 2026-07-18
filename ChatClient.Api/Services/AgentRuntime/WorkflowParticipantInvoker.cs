using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Services.AgentRuntime;

namespace ChatClient.Api.Services.AgentRuntime;

public interface IWorkflowParticipantInvoker
{
    WorkflowParticipantInvocationHandle CreateInvocation(
        ResolvedWorkflowParticipant participant,
        AgentRunContext parentContext);

    IAsyncEnumerable<AgentRunEvent> InvokeAsync(
        WorkflowParticipantInvocationHandle invocation,
        AgentRuntimeRunRequest request,
        AgentRuntimeCreationContext creationContext,
        CancellationToken cancellationToken = default);
}

public sealed record WorkflowParticipantInvocationHandle
{
    public required ResolvedWorkflowParticipant Participant { get; init; }

    public required AgentRunContext Context { get; init; }
}

public sealed class WorkflowParticipantInvoker(
    IAgentRunContextFactory contextFactory,
    IAgentRunner agentRunner,
    IInlineLlmAgentRuntimeFactory inlineRuntimeFactory,
    IAgentRuntimeProtocolExecutor protocolExecutor) : IWorkflowParticipantInvoker
{
    public WorkflowParticipantInvocationHandle CreateInvocation(
        ResolvedWorkflowParticipant participant,
        AgentRunContext parentContext)
    {
        var childDefinition = CreateInvocationDefinition(participant);
        var childContext = contextFactory.CreateChild(
            parentContext,
            childDefinition,
            new WorkflowParticipantInvocation(
                participant.ParticipantId,
                participant.DisplayName));

        return new WorkflowParticipantInvocationHandle
        {
            Participant = participant,
            Context = childContext
        };
    }

    private static AgentDefinitionDescriptor CreateInvocationDefinition(
        ResolvedWorkflowParticipant participant)
    {
        var reference = participant.Source is ReferencedParticipantSource referenced
            ? referenced.Reference
            : new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, participant.ParticipantId);

        return new AgentDefinitionDescriptor
        {
            Reference = reference,
            Name = participant.DisplayName,
            Description = participant.Summary,
            RuntimeKind = participant.RuntimeKind,
            ModelRequirement = participant.RuntimeKind == AgentRuntimeKind.LlmAgent
                ? AgentModelRequirement.Required
                : AgentModelRequirement.None
        };
    }

    public async IAsyncEnumerable<AgentRunEvent> InvokeAsync(
        WorkflowParticipantInvocationHandle invocation,
        AgentRuntimeRunRequest request,
        AgentRuntimeCreationContext creationContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await foreach (var runEvent in InvokeCoreAsync(
                           invocation.Participant,
                           request,
                           creationContext,
                           invocation.Context,
                           cancellationToken))
        {
            yield return runEvent;
        }
    }

    private async IAsyncEnumerable<AgentRunEvent> InvokeCoreAsync(
        ResolvedWorkflowParticipant participant,
        AgentRuntimeRunRequest request,
        AgentRuntimeCreationContext creationContext,
        AgentRunContext childContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        switch (participant.Source)
        {
            case ReferencedParticipantSource referenced:
                await foreach (var runEvent in agentRunner.RunAsync(
                                   referenced.Reference,
                                   request,
                                   creationContext,
                                   childContext,
                                   cancellationToken))
                {
                    yield return runEvent;
                }

                yield break;

            case MaterializedLlmParticipantSource materialized:
                var runtime = inlineRuntimeFactory.Create(
                    new AgentRuntimeDescriptor(
                        participant.ParticipantId,
                        participant.DisplayName,
                        participant.Summary,
                        AgentRuntimeKind.LlmAgent),
                    materialized.Agent,
                    creationContext);
                await foreach (var runEvent in protocolExecutor.RunAsync(
                                   runtime,
                                   request,
                                   childContext,
                                   cancellationToken))
                {
                    yield return runEvent;
                }

                yield break;

            case RuntimeWorkflowParticipantSource runtimeSource:
                await foreach (var runEvent in protocolExecutor.RunAsync(
                                   runtimeSource.Runtime,
                                   request,
                                   childContext,
                                   cancellationToken))
                {
                    yield return runEvent;
                }

                yield break;

            default:
                yield return new AgentRunFailed(new AgentRunError(
                    "invalid_workflow",
                    $"Workflow participant '{participant.ParticipantId}' has no executable source.",
                    false));
                yield break;
        }
    }

}

public sealed record RuntimeWorkflowParticipantSource(IAgentRuntime Runtime)
    : ResolvedWorkflowParticipantSource;
