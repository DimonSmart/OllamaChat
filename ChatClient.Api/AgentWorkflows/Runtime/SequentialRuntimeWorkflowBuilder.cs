using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace ChatClient.Api.AgentWorkflows.Runtime;

public sealed class SequentialRuntimeWorkflowBuilder : IOrchestrationRuntimeWorkflowBuilder
{
    public bool CanBuild(IOrchestrationWorkflowDefinition workflow) =>
        workflow is SequentialWorkflowDefinition;

    public Workflow Build(
        IOrchestrationWorkflowDefinition workflow,
        IReadOnlyDictionary<string, AIAgent> agentsById,
        OrchestrationRuntimeBuildContext context)
    {
        var sequentialWorkflow = workflow as SequentialWorkflowDefinition
                                 ?? throw new InvalidOperationException(
                                     $"Workflow kind '{workflow.Kind}' is not supported by {nameof(SequentialRuntimeWorkflowBuilder)}.");

        var orderedAgents = sequentialWorkflow.ParticipantOrder
            .Select(agentId => agentsById.TryGetValue(agentId, out var agent)
                ? agent
                : throw new InvalidOperationException(
                    $"Workflow agent '{agentId}' was not prepared."))
            .ToList();

        return AgentWorkflowBuilder.BuildSequential(
            sequentialWorkflow.DisplayName,
            orderedAgents);
    }
}
