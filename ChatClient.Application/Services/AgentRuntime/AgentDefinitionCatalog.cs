using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.AgentRuntime;

public sealed class AgentDefinitionCatalog(
    IAgentTemplateService agentTemplateService,
    IWorkflowDefinitionService workflowDefinitionService,
    IAgentInputDefinitionProvider inputDefinitionProvider,
    IAgentDefinitionModelRequirementAnalyzer modelRequirementAnalyzer) : IAgentDefinitionCatalog
{
    public async Task<IReadOnlyList<AgentDefinitionDescriptor>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var agents = await agentTemplateService.GetAllAsync();
        var workflows = await workflowDefinitionService.GetAllAsync();

        return agents.Select(CreateAgentDescriptor)
            .Concat(await Task.WhenAll(workflows.Select(workflow => CreateWorkflowDescriptorAsync(workflow, cancellationToken))))
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

        if (!Guid.TryParse(reference.Id, out var id))
            return null;

        return reference.Kind switch
        {
            AgentDefinitionKind.SavedAgent => await agentTemplateService.GetByIdAsync(id) is { } agent
                ? CreateAgentDescriptor(agent)
                : null,
            AgentDefinitionKind.SavedWorkflow => await workflowDefinitionService.GetByIdAsync(id) is { } workflow
                ? await CreateWorkflowDescriptorAsync(workflow, cancellationToken)
                : null,
            _ => null
        };
    }

    public async Task<AgentDefinitionDescriptor> GetRequiredAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken = default) =>
        await FindAsync(reference, cancellationToken) ??
        throw new KeyNotFoundException($"Saved definition '{reference.Kind}:{reference.Id}' was not found.");

    private static AgentDefinitionDescriptor CreateAgentDescriptor(Domain.Models.AgentTemplateDefinition agent) =>
        new()
        {
            Reference = new AgentDefinitionReference(
                AgentDefinitionKind.SavedAgent,
                agent.Id.ToString("D")),
            Name = agent.AgentName,
            Description = agent.Summary,
            RuntimeKind = AgentRuntimeKind.LlmAgent,
            AvatarText = agent.AvatarText,
            ConfiguredModel = new ServerModelSelection(agent.LlmId, agent.ModelName),
            ModelRequirement = AgentModelRequirement.Required,
            LaunchCapabilities = new AgentLaunchCapabilities
            {
                SupportsMcpBindingOverrides = true
            },
            DefaultMcpServerBindings = agent.McpServerBindings
                .Select(static binding => binding.Clone())
                .ToList(),
            SupportsAttachments = true
        };

    private async Task<AgentDefinitionDescriptor> CreateWorkflowDescriptorAsync(
        Domain.Models.SavedWorkflowDefinition workflow,
        CancellationToken cancellationToken)
    {
        var reference = new AgentDefinitionReference(
            AgentDefinitionKind.SavedWorkflow,
            workflow.Id.ToString("D"));
        IReadOnlyList<AgentInputDefinition> inputs = [];
        var requirement = AgentModelRequirement.Required;
        var definitionProblems = new List<AgentDefinitionProblem>();

        try
        {
            inputs = await inputDefinitionProvider.GetInputsAsync(reference, cancellationToken);
            requirement = await modelRequirementAnalyzer.AnalyzeAsync(reference, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            definitionProblems.Add(new AgentDefinitionProblem($"Invalid workflow definition: {ex.Message}"));
        }

        return new AgentDefinitionDescriptor
        {
            Reference = reference,
            Name = workflow.DisplayName,
            Description = workflow.Description,
            RuntimeKind = AgentRuntimeKind.WorkflowAgent,
            AvatarText = "WF",
            Inputs = inputs,
            ModelRequirement = requirement,
            SupportsAttachments = true,
            DefinitionProblems = definitionProblems
        };
    }
}
