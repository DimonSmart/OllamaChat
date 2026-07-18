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
    IAgentDefinitionCatalog definitionCatalog,
    IAgentRunContextFactory contextFactory,
    IAgentRunner agentRunner,
    IInlineLlmAgentRuntimeFactory inlineRuntimeFactory,
    IAgentRuntimeProtocolExecutor protocolExecutor) : IWorkflowParticipantInvoker
{
    public async IAsyncEnumerable<AgentRunEvent> InvokeAsync(
        ResolvedWorkflowParticipant participant,
        AgentRuntimeRunRequest request,
        AgentRuntimeCreationContext creationContext,
        AgentRunContext parentContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var childDefinition = await ResolveChildDefinitionAsync(
            participant,
            cancellationToken);
        var childContext = contextFactory.CreateChild(
            parentContext,
            childDefinition,
            new WorkflowParticipantInvocation(
                participant.ParticipantId,
                participant.DisplayName));

        await foreach (var runEvent in InvokeCoreAsync(
                           participant,
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

    private async Task<AgentDefinitionDescriptor> ResolveChildDefinitionAsync(
        ResolvedWorkflowParticipant participant,
        CancellationToken cancellationToken)
    {
        if (participant.Source is ReferencedParticipantSource referenced)
        {
            return await definitionCatalog.GetRequiredAsync(
                referenced.Reference,
                cancellationToken);
        }

        return new AgentDefinitionDescriptor
        {
            Reference = new AgentDefinitionReference(
                AgentDefinitionKind.SavedAgent,
                participant.ParticipantId),
            Name = participant.DisplayName,
            Description = participant.Summary,
            RuntimeKind = AgentRuntimeKind.LlmAgent,
            ModelRequirement = AgentModelRequirement.Required
        };
    }
}

public sealed record RuntimeWorkflowParticipantSource(IAgentRuntime Runtime)
    : ResolvedWorkflowParticipantSource;
