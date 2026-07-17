using ChatClient.Application.Services;

namespace ChatClient.Application.Services.AgentRuntime;

public sealed class AgentDefinitionCatalog(
    IAgentTemplateService agentTemplateService,
    IWorkflowDefinitionService workflowDefinitionService) : IAgentDefinitionCatalog
{
    public async Task<IReadOnlyList<AgentDefinitionCatalogItem>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var agents = await agentTemplateService.GetAllAsync();
        var workflows = await workflowDefinitionService.GetAllAsync();

        return agents
            .Select(static agent => new AgentDefinitionCatalogItem
            {
                Reference = new AgentDefinitionReference(
                    AgentDefinitionKind.SavedAgent,
                    agent.Id.ToString("D")),
                Name = agent.AgentName,
                Description = agent.Summary,
                RuntimeKind = AgentRuntimeKind.LlmAgent
            })
            .Concat(workflows.Select(static workflow => new AgentDefinitionCatalogItem
            {
                Reference = new AgentDefinitionReference(
                    AgentDefinitionKind.SavedWorkflow,
                    workflow.Id.ToString("D")),
                Name = workflow.DisplayName,
                Description = workflow.Description,
                RuntimeKind = AgentRuntimeKind.WorkflowAgent
            }))
            .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Reference.Kind)
            .ThenBy(static item => item.Reference.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<AgentDefinitionCatalogItem?> FindAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return (await GetAllAsync(cancellationToken)).FirstOrDefault(item =>
            item.Reference.Kind == reference.Kind &&
            string.Equals(item.Reference.Id, reference.Id, StringComparison.OrdinalIgnoreCase));
    }
}
