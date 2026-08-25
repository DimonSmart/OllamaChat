using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.AgentRuntime;

public sealed class AgentDefinitionCatalog(
    IAgentTemplateService agentTemplateService,
    IWorkflowDefinitionService workflowDefinitionService,
    IAgentInputDefinitionProvider inputDefinitionProvider,
    IAgentDefinitionModelRequirementAnalyzer modelRequirementAnalyzer,
    IAgentDefinitionLaunchCapabilityAnalyzer launchCapabilityAnalyzer,
    IAgentDefinitionLaunchBehaviorAnalyzer launchBehaviorAnalyzer) : IAgentDefinitionCatalog
{
    public async Task<IReadOnlyList<AgentDefinitionDescriptor>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var agents = await agentTemplateService.GetAllAsync();
        var workflows = await workflowDefinitionService.GetAllAsync();

        var agentDescriptors = await Task.WhenAll(
            agents.Select(agent => CreateAgentDescriptorAsync(agent, cancellationToken)));
        var workflowDescriptors = await Task.WhenAll(
            workflows.Select(workflow => CreateWorkflowDescriptorAsync(workflow, cancellationToken)));

        return agentDescriptors
            .Concat(workflowDescriptors)
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
                ? await CreateAgentDescriptorAsync(agent, cancellationToken)
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

    private async Task<AgentDefinitionDescriptor> CreateAgentDescriptorAsync(
        Domain.Models.AgentTemplateDefinition agent,
        CancellationToken cancellationToken)
    {
        var reference = new AgentDefinitionReference(
            AgentDefinitionKind.SavedAgent,
            agent.Id.ToString("D"));
        var definitionProblems = new List<AgentDefinitionProblem>();
        var launchCapabilities = new AgentLaunchCapabilities
        {
            SupportsMcpBindingOverrides = true,
            SupportsWorkspace = agent.FileAccessProviderProfileId is not null || agent.EnableShell,
            SupportsSandboxProfile = agent.EnableShell
        };

        try
        {
            var effectiveCapabilities = await launchCapabilityAnalyzer.AnalyzeAsync(reference, cancellationToken);
            launchCapabilities = new AgentLaunchCapabilities
            {
                SupportsMcpBindingOverrides = true,
                SupportsWorkspace = effectiveCapabilities.SupportsWorkspace,
                SupportsSandboxProfile = effectiveCapabilities.SupportsSandboxProfile
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            definitionProblems.Add(new AgentDefinitionProblem($"Invalid saved agent definition: {ex.Message}"));
        }

        return new AgentDefinitionDescriptor
        {
            Reference = reference,
            Name = agent.AgentName,
            Description = agent.Summary,
            RuntimeKind = AgentRuntimeKind.LlmAgent,
            AvatarText = agent.AvatarText ?? string.Empty,
            ConfiguredModel = new ServerModelSelection(agent.LlmId, agent.ModelName),
            ModelRequirement = AgentModelRequirement.Required,
            LaunchCapabilities = launchCapabilities,
            DefaultMcpServerBindings = agent.McpServerBindings
                .Select(static binding => binding.Clone())
                .ToList(),
            SupportsAttachments = true,
            DefinitionProblems = definitionProblems
        };
    }

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
        var launchCapabilities = new AgentLaunchCapabilities();
        var launchBehavior = AgentLaunchBehavior.WaitForUserMessage;

        try
        {
            inputs = await inputDefinitionProvider.GetInputsAsync(reference, cancellationToken);
            requirement = await modelRequirementAnalyzer.AnalyzeAsync(reference, cancellationToken);
            launchCapabilities = await launchCapabilityAnalyzer.AnalyzeAsync(reference, cancellationToken);
            launchBehavior = await launchBehaviorAnalyzer.AnalyzeAsync(reference, cancellationToken);
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
            LaunchCapabilities = launchCapabilities,
            LaunchBehavior = launchBehavior,
            SupportsAttachments = true,
            DefinitionProblems = definitionProblems
        };
    }
}
