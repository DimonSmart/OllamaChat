namespace ChatClient.Application.Services.AgentRuntime;

public sealed class AgentRuntimeFactory(
    ILlmAgentRuntimeFactory llmAgentRuntimeFactory,
    IWorkflowAgentRuntimeFactory workflowAgentRuntimeFactory) : IAgentRuntimeFactory
{
    public Task<IAgentRuntime> CreateAsync(
        AgentDefinitionReference reference,
        AgentRuntimeCreationContext context,
        CancellationToken cancellationToken = default) =>
        reference.Kind switch
        {
            AgentDefinitionKind.SavedAgent => llmAgentRuntimeFactory.CreateAsync(
                reference.Id,
                context,
                cancellationToken),
            AgentDefinitionKind.SavedWorkflow => workflowAgentRuntimeFactory.CreateAsync(
                reference.Id,
                context,
                cancellationToken),
            _ => throw new NotSupportedException(
                $"Agent definition kind '{reference.Kind}' is not supported.")
        };
}
