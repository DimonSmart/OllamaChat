using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace ChatClient.Api.AgentWorkflows.Runtime;

public sealed class HandoffRuntimeWorkflowBuilder : IOrchestrationRuntimeWorkflowBuilder
{
    public bool CanBuild(IOrchestrationWorkflowDefinition workflow) =>
        workflow is AgentWorkflowDefinition;

    [Obsolete]
    public Workflow Build(
        IOrchestrationWorkflowDefinition workflow,
        IReadOnlyDictionary<string, AIAgent> agentsById,
        OrchestrationRuntimeBuildContext context)
    {
        var handoffWorkflow = workflow as AgentWorkflowDefinition
                              ?? throw new InvalidOperationException(
                                  $"Workflow kind '{workflow.Kind}' is not supported by {nameof(HandoffRuntimeWorkflowBuilder)}.");

        if (!agentsById.TryGetValue(handoffWorkflow.StartParticipantId, out var startAgent))
        {
            throw new InvalidOperationException(
                $"Workflow start participant '{handoffWorkflow.StartParticipantId}' was not prepared.");
        }

#pragma warning disable MAAIW001
        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(startAgent);
#pragma warning restore MAAIW001

        foreach (var handoff in SelectHandoffsForRuntime(handoffWorkflow, context.AssistantSpeakerIds))
        {
            if (!agentsById.TryGetValue(handoff.FromParticipantId, out var fromAgent))
            {
                throw new InvalidOperationException(
                    $"Workflow source participant '{handoff.FromParticipantId}' was not prepared.");
            }

            if (!agentsById.TryGetValue(handoff.ToParticipantId, out var toAgent))
            {
                throw new InvalidOperationException(
                    $"Workflow target participant '{handoff.ToParticipantId}' was not prepared.");
            }

            builder.WithHandoff(fromAgent, toAgent, handoff.Label);
        }

        return builder.Build();
    }

    internal static IReadOnlyList<AgentWorkflowHandoffDefinition> SelectDistinctHandoffsForRuntime(
        IReadOnlyCollection<AgentWorkflowHandoffDefinition> handoffs)
    {
        var selectedHandoffsByPair = new Dictionary<string, AgentWorkflowHandoffDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var handoff in handoffs)
        {
            var handoffKey = $"{handoff.FromParticipantId}\u001F{handoff.ToParticipantId}";
            if (!selectedHandoffsByPair.TryGetValue(handoffKey, out var existingHandoff))
            {
                selectedHandoffsByPair[handoffKey] = handoff;
                continue;
            }

            if (existingHandoff.IsFallback && !handoff.IsFallback)
            {
                selectedHandoffsByPair[handoffKey] = handoff;
            }
        }

        return selectedHandoffsByPair.Values.ToList();
    }

    [Obsolete]
    internal static IReadOnlyList<AgentWorkflowHandoffDefinition> SelectHandoffsForRuntime(
        AgentWorkflowDefinition definition,
        IReadOnlyList<string> assistantSpeakerIds)
    {
        var handoffs = SelectDistinctHandoffsForRuntime(definition.Handoffs);
        if (assistantSpeakerIds.Count == 0)
        {
            return handoffs;
        }

        var agentsById = definition.Agents.ToDictionary(
            static agent => agent.Id,
            StringComparer.OrdinalIgnoreCase);
        var turnCountsByAgentId = assistantSpeakerIds
            .GroupBy(static speakerId => speakerId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);

        List<AgentWorkflowHandoffDefinition> selected = [];

        foreach (var handoff in handoffs)
        {
            if (!agentsById.TryGetValue(handoff.ToParticipantId, out var targetAgent))
            {
                selected.Add(handoff);
                continue;
            }

            if (targetAgent.MaxTurnsPerSession > 0 &&
                turnCountsByAgentId.TryGetValue(targetAgent.Id, out var usedTurns) &&
                usedTurns >= targetAgent.MaxTurnsPerSession)
            {
                continue;
            }

            if (targetAgent.MinAssistantTurnsBetweenTurns > 0 &&
                CountAssistantTurnsSinceLastSpeaker(assistantSpeakerIds, targetAgent.Id) < targetAgent.MinAssistantTurnsBetweenTurns)
            {
                continue;
            }

            selected.Add(handoff);
        }

        return selected;
    }

    internal static int CountAssistantTurnsSinceLastSpeaker(
        IReadOnlyList<string> assistantSpeakerIds,
        string speakerId)
    {
        var turnsSinceLastSpeaker = 0;

        for (var index = assistantSpeakerIds.Count - 1; index >= 0; index--)
        {
            if (string.Equals(assistantSpeakerIds[index], speakerId, StringComparison.OrdinalIgnoreCase))
            {
                return turnsSinceLastSpeaker;
            }

            turnsSinceLastSpeaker++;
        }

        return int.MaxValue;
    }
}
