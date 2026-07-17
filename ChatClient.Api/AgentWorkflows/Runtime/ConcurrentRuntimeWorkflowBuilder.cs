using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.AgentWorkflows.Runtime;

public sealed class ConcurrentRuntimeWorkflowBuilder : IOrchestrationRuntimeWorkflowBuilder
{
    public bool CanBuild(IOrchestrationWorkflowDefinition workflow) =>
        workflow is ConcurrentWorkflowDefinition;

    public Workflow Build(
        IOrchestrationWorkflowDefinition workflow,
        IReadOnlyDictionary<string, AIAgent> agentsById,
        OrchestrationRuntimeBuildContext context)
    {
        var concurrentWorkflow = workflow as ConcurrentWorkflowDefinition
                                 ?? throw new InvalidOperationException(
                                     $"Workflow kind '{workflow.Kind}' is not supported by {nameof(ConcurrentRuntimeWorkflowBuilder)}.");

        var participantAgents = concurrentWorkflow.ParticipantIds
            .Select(agentId => agentsById.TryGetValue(agentId, out var agent)
                ? agent
                : throw new InvalidOperationException(
                    $"Workflow participant '{agentId}' was not prepared."))
            .ToList();

        var aggregator = CreateAggregator(concurrentWorkflow.Aggregation);
        return AgentWorkflowBuilder.BuildConcurrent(
            concurrentWorkflow.DisplayName,
            participantAgents,
            aggregator);
    }

    private static Func<IList<List<ChatMessage>>, List<ChatMessage>>? CreateAggregator(
        ConcurrentWorkflowAggregationDefinition aggregation) =>
        aggregation.Kind switch
        {
            ConcurrentWorkflowAggregationKind.LastMessagePerAgent => null,
            ConcurrentWorkflowAggregationKind.ConcatenateAllMessages => messageBatches =>
                messageBatches.SelectMany(static batch => batch).ToList(),
            _ => throw new InvalidOperationException(
                $"Unsupported concurrent aggregation kind '{aggregation.Kind}'.")
        };
}
