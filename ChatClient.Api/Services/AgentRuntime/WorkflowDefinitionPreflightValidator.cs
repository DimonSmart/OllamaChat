using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Compatibility;
using ChatClient.Application.Services;
using ChatClient.Application.Services.AgentRuntime;

namespace ChatClient.Api.Services.AgentRuntime;

public sealed class WorkflowDefinitionPreflightValidator(
    IWorkflowDefinitionService workflowDefinitionService,
    IWorkflowDefinitionCompiler workflowDefinitionCompiler,
    ILegacyWorkflowDefinitionNormalizer legacyWorkflowDefinitionNormalizer,
    IWorkflowParticipantResolver workflowParticipantResolver) : IWorkflowDefinitionPreflightValidator
{
    public async Task<IReadOnlyList<AgentDefinitionLaunchProblem>> ValidateAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reference.Kind != AgentDefinitionKind.SavedWorkflow)
        {
            return [];
        }

        var problems = new List<AgentDefinitionLaunchProblem>();
        if (!Guid.TryParse(reference.Id, out var workflowId))
        {
            return [new AgentDefinitionLaunchProblem($"Workflow id '{reference.Id}' is not a valid saved-workflow id.")];
        }

        var savedWorkflow = await workflowDefinitionService.GetByIdAsync(workflowId);
        if (savedWorkflow is null)
        {
            return [new AgentDefinitionLaunchProblem($"Saved workflow '{reference.Id}' was not found.")];
        }

        try
        {
            var compiled = await workflowDefinitionCompiler.CompileAsync(
                savedWorkflow.SourceCode,
                cancellationToken);
            var compiledWorkflow = compiled.Workflow;
            if (compiledWorkflow is null)
            {
                return [new AgentDefinitionLaunchProblem("Workflow compilation did not return a workflow definition.")];
            }

            var workflow = await legacyWorkflowDefinitionNormalizer.NormalizeAsync(
                compiledWorkflow,
                cancellationToken);

            var participants = await workflowParticipantResolver.ResolveAsync(workflow, cancellationToken);
            if (workflow is not SequentialWorkflowDefinition &&
                participants.Any(static participant => participant.RuntimeKind == AgentRuntimeKind.WorkflowAgent))
            {
                problems.Add(new AgentDefinitionLaunchProblem(
                    "Saved workflow participants are currently supported only in sequential workflows."));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problems.Add(new AgentDefinitionLaunchProblem(ex.Message));
        }

        return problems;
    }
}
