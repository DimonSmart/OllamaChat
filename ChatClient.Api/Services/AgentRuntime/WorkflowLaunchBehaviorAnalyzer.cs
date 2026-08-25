using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Compatibility;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;

namespace ChatClient.Api.Services.AgentRuntime;

public sealed class WorkflowLaunchBehaviorAnalyzer(
    IWorkflowDefinitionService workflowDefinitionService,
    IWorkflowDefinitionCompiler workflowDefinitionCompiler,
    ILegacyWorkflowDefinitionNormalizer legacyWorkflowDefinitionNormalizer) : IAgentDefinitionLaunchBehaviorAnalyzer
{
    public async Task<AgentLaunchBehavior> AnalyzeAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken = default)
    {
        if (reference.Kind == AgentDefinitionKind.SavedAgent)
            return AgentLaunchBehavior.WaitForUserMessage;

        if (!Guid.TryParse(reference.Id, out var workflowId))
            throw new KeyNotFoundException($"Workflow id '{reference.Id}' is not a valid saved-workflow id.");

        var savedWorkflow = await workflowDefinitionService.GetByIdAsync(workflowId)
            ?? throw new KeyNotFoundException($"Saved workflow '{reference.Id}' was not found.");
        var compiled = await workflowDefinitionCompiler.CompileAsync(savedWorkflow.SourceCode, cancellationToken);
        var definition = await legacyWorkflowDefinitionNormalizer.NormalizeAsync(
            compiled.Workflow ?? throw new InvalidOperationException("Workflow compilation did not return a workflow definition."),
            cancellationToken);

        return definition.Execution.Mode == AgentWorkflowExecutionMode.Autonomous
            ? AgentLaunchBehavior.RunOnStart
            : AgentLaunchBehavior.WaitForUserMessage;
    }
}
