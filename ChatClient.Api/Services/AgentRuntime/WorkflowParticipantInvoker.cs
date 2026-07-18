using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Services.AgentRuntime;

namespace ChatClient.Api.Services.AgentRuntime;

public interface IWorkflowParticipantInvoker
{
    IAsyncEnumerable<AgentRunEvent> InvokeAsync(
        ResolvedWorkflowParticipant participant,
        AgentRuntimeRunRequest request,
        AgentRuntimeCreationContext creationContext,
        AgentRunContext parentContext,
        CancellationToken cancellationToken = default);
}

public sealed class WorkflowParticipantInvoker(
    IAgentRunContextFactory contextFactory,
    Func<IAgentRunner> runnerFactory,
    IInlineLlmAgentRuntimeFactory inlineRuntimeFactory,
    IAgentRunNestingValidator nestingValidator,
    IAgentRuntimeProtocolExecutor protocolExecutor) : IWorkflowParticipantInvoker
{
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
        ResolvedWorkflowParticipant participant,
        AgentRuntimeRunRequest request,
        AgentRuntimeCreationContext creationContext,
        AgentRunContext parentContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var childDefinition = CreateInvocationDefinition(participant);
        var childContext = contextFactory.CreateChild(
            parentContext,
            childDefinition,
            new WorkflowParticipantInvocation(
                participant.ParticipantId,
                participant.DisplayName));

        await foreach (var runEvent in InvokeCoreAsync(
                           participant,
                           childDefinition,
                           request,
                           creationContext,
                           childContext,
                           cancellationToken))
        {
            yield return runEvent;
        }
    }

    private async IAsyncEnumerable<AgentRunEvent> InvokeCoreAsync(
        ResolvedWorkflowParticipant participant,
        AgentDefinitionDescriptor childDefinition,
        AgentRuntimeRunRequest request,
        AgentRuntimeCreationContext creationContext,
        AgentRunContext childContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        switch (participant.Source)
        {
            case ReferencedParticipantSource referenced:
                var runner = runnerFactory();
                await foreach (var runEvent in runner.RunAsync(
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
                var validation = nestingValidator.Validate(childDefinition, childContext);
                if (!validation.IsValid)
                {
                    yield return new AgentRunFailed(validation.Error!);
                    yield break;
                }

                await foreach (var runEvent in protocolExecutor.ExecuteAsync(runtime, request, childContext, cancellationToken))
                {
                    yield return runEvent;
                }

                yield break;

            case RuntimeWorkflowParticipantSource runtimeSource:
                var runtimeValidation = nestingValidator.Validate(childDefinition, childContext);
                if (!runtimeValidation.IsValid)
                {
                    yield return new AgentRunFailed(runtimeValidation.Error!);
                    yield break;
                }

                await foreach (var runEvent in protocolExecutor.ExecuteAsync(runtimeSource.Runtime, request, childContext, cancellationToken))
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
