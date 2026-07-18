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

public sealed class WorkflowParticipantRuntimeFactory(
    IInlineLlmAgentRuntimeFactory inlineRuntimeFactory,
    IServiceProvider serviceProvider) : IWorkflowParticipantRuntimeFactory
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
            ReferencedParticipantSource referenced => CreateReferencedAsync(
                participant,
                referenced,
                creationContext,
                cancellationToken),
            _ => throw new NotSupportedException(
                $"Workflow participant '{participant.ParticipantId}' has unsupported source '{participant.Source.GetType().Name}'.")
        };
    }

    private WorkflowRuntimeParticipant CreateMaterialized(
        ResolvedWorkflowParticipant participant,
        MaterializedLlmParticipantSource source,
        AgentRuntimeCreationContext creationContext)
    {
        var runtime = inlineRuntimeFactory.Create(
            new AgentRuntimeDescriptor(
                participant.ParticipantId,
                participant.DisplayName,
                participant.Summary,
                AgentRuntimeKind.LlmAgent),
            source.Agent,
            creationContext);

        return new WorkflowRuntimeParticipant
        {
            Id = participant.ParticipantId,
            DisplayName = participant.DisplayName,
            Summary = participant.Summary,
            Runtime = runtime
        };
    }

    private async Task<WorkflowRuntimeParticipant> CreateReferencedAsync(
        ResolvedWorkflowParticipant participant,
        ReferencedParticipantSource source,
        AgentRuntimeCreationContext creationContext,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        return new WorkflowRuntimeParticipant
        {
            Id = participant.ParticipantId,
            DisplayName = participant.DisplayName,
            Summary = participant.Summary,
            Runtime = new ReferencedWorkflowParticipantRuntime(
                new AgentRuntimeDescriptor(
                    participant.ParticipantId,
                    participant.DisplayName,
                    participant.Summary,
                    participant.RuntimeKind),
                source.Reference,
                creationContext,
                serviceProvider),
            DefinitionReference = source.Reference
        };
    }
}

file sealed class ReferencedWorkflowParticipantRuntime(
    AgentRuntimeDescriptor descriptor,
    AgentDefinitionReference reference,
    AgentRuntimeCreationContext creationContext,
    IServiceProvider serviceProvider) : IAgentRuntime
{
    public AgentRuntimeDescriptor Descriptor { get; } = descriptor;

    public async IAsyncEnumerable<AgentRunEvent> RunAsync(
        AgentRuntimeRunRequest request,
        AgentRunContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var runner = serviceProvider.GetRequiredService<IAgentRunner>();
        await foreach (var runEvent in runner.RunAsync(
                           reference,
                           request,
                           creationContext,
                           context,
                           cancellationToken))
        {
            yield return runEvent;
        }
    }
}

public sealed class AgentRunContextFactory : IAgentRunContextFactory
{
    public AgentRunContext CreateChild(
        AgentRunContext parent,
        AgentDefinitionReference? childDefinition)
    {
        ArgumentNullException.ThrowIfNull(parent);

        var path = childDefinition is null
            ? parent.DefinitionPath
            : parent.DefinitionPath.Concat([childDefinition]).ToList();

        return new AgentRunContext
        {
            RunId = Guid.NewGuid().ToString("N"),
            ParentRunId = parent.RunId,
            ConversationId = parent.ConversationId,
            DefinitionPath = path
        };
    }
}
