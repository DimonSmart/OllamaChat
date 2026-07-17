using ChatClient.Application.Services;

namespace ChatClient.Application.Services.AgentRuntime;

public sealed class AgentDefinitionCatalog(
    IAgentTemplateService agentTemplateService,
    IWorkflowDefinitionService workflowDefinitionService,
    IAgentInputDefinitionProvider inputDefinitionProvider) : IAgentDefinitionCatalog
{
    public async Task<IReadOnlyList<AgentDefinitionDescriptor>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var agents = await agentTemplateService.GetAllAsync();
        var workflows = await workflowDefinitionService.GetAllAsync();

        return agents
            .Select(static agent => new AgentDefinitionDescriptor
            {
                Reference = new AgentDefinitionReference(
                    AgentDefinitionKind.SavedAgent,
                    agent.Id.ToString("D")),
                Name = agent.AgentName,
                Description = agent.Summary,
                RuntimeKind = AgentRuntimeKind.LlmAgent,
                AvatarText = agent.AvatarText,
                ModelRequirement = AgentModelRequirement.Required,
                SupportsAttachments = true
            })
            .Concat(await Task.WhenAll(workflows.Select(async workflow => new AgentDefinitionDescriptor
            {
                Reference = new AgentDefinitionReference(
                    AgentDefinitionKind.SavedWorkflow,
                    workflow.Id.ToString("D")),
                Name = workflow.DisplayName,
                Description = workflow.Description,
                RuntimeKind = AgentRuntimeKind.WorkflowAgent,
                AvatarText = "WF",
                Inputs = await inputDefinitionProvider.GetInputsAsync(
                    new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, workflow.Id.ToString("D")),
                    cancellationToken),
                ModelRequirement = AgentModelRequirement.Required,
                SupportsAttachments = true
            })))
            .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Reference.Kind)
            .ThenBy(static item => item.Reference.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<AgentDefinitionDescriptor?> FindAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return (await GetAllAsync(cancellationToken)).FirstOrDefault(item =>
            item.Reference.Kind == reference.Kind &&
            string.Equals(item.Reference.Id, reference.Id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AgentDefinitionDescriptor> GetRequiredAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken = default) =>
        await FindAsync(reference, cancellationToken) ??
        throw new KeyNotFoundException($"Saved definition '{reference.Kind}:{reference.Id}' was not found.");
}
