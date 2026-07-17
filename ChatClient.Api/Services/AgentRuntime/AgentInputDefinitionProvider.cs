using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Services;
using ChatClient.Application.Services.AgentRuntime;

namespace ChatClient.Api.Services.AgentRuntime;

public sealed class AgentInputDefinitionProvider(
    IWorkflowDefinitionService workflowDefinitionService,
    IWorkflowDefinitionCompiler workflowDefinitionCompiler) : IAgentInputDefinitionProvider
{
    public async Task<IReadOnlyList<AgentInputDefinition>> GetInputsAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (reference.Kind == AgentDefinitionKind.SavedAgent)
        {
            return [];
        }

        if (!Guid.TryParse(reference.Id, out var workflowId))
        {
            throw new KeyNotFoundException($"Workflow id '{reference.Id}' is not a valid saved-workflow id.");
        }

        var workflow = await workflowDefinitionService.GetByIdAsync(workflowId);
        if (workflow is null)
        {
            throw new KeyNotFoundException($"Saved workflow '{reference.Id}' was not found.");
        }

        var compiled = await workflowDefinitionCompiler.CompileAsync(
            workflow.SourceCode,
            cancellationToken);
        var definition = compiled.Workflow
            ?? throw new InvalidOperationException("Workflow compilation did not return a workflow definition.");

        return definition.StartInputs
            .Select(static input => new AgentInputDefinition
            {
                Key = input.Key,
                DisplayName = input.DisplayName,
                Description = input.Description,
                Kind = MapKind(input.Kind),
                IsRequired = input.IsRequired,
                Placeholder = input.Placeholder,
                DefaultValue = input.DefaultValue
            })
            .ToList();
    }

    private static AgentInputDefinitionKind MapKind(WorkflowStartInputKind kind) =>
        kind switch
        {
            WorkflowStartInputKind.Text => AgentInputDefinitionKind.Text,
            WorkflowStartInputKind.Number => AgentInputDefinitionKind.Number,
            WorkflowStartInputKind.Boolean => AgentInputDefinitionKind.Boolean,
            WorkflowStartInputKind.Json => AgentInputDefinitionKind.Json,
            WorkflowStartInputKind.MarkdownDocument => AgentInputDefinitionKind.MarkdownDocument,
            _ => AgentInputDefinitionKind.Text
        };
}
