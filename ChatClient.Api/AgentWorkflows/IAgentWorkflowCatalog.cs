namespace ChatClient.Api.AgentWorkflows;

public interface IAgentWorkflowCatalog
{
    Task<IReadOnlyList<AgentWorkflowTemplate>> ListAsync(CancellationToken cancellationToken = default);

    Task<AgentWorkflowTemplate> GetRequiredAsync(string workflowId, CancellationToken cancellationToken = default);
}
