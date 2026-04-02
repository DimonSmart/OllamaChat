using ChatClient.Api.AgentWorkflows;
namespace ChatClient.Api.Client.Services.Agentic;

internal static class WorkflowSpeakerResolver
{
    public static string? ResolveSpeakerId(
        string? executorId,
        IReadOnlyDictionary<string, string> agentIdsByExecutorId,
        IOrchestrationWorkflowDefinition? workflow,
        int assistantMessageIndex)
    {
        return ResolveFromExecutorId(executorId, agentIdsByExecutorId)
            ?? ResolveFromWorkflow(workflow, assistantMessageIndex);
    }

    public static string? ResolveFromExecutorId(
        string? executorId,
        IReadOnlyDictionary<string, string> agentIdsByExecutorId)
    {
        if (!string.IsNullOrWhiteSpace(executorId) &&
            agentIdsByExecutorId.TryGetValue(executorId, out var speakerId))
        {
            return speakerId;
        }

        return null;
    }

    public static string? ResolveFromWorkflow(
        IOrchestrationWorkflowDefinition? workflow,
        int assistantMessageIndex)
    {
        if (workflow is null || assistantMessageIndex < 0)
        {
            return null;
        }

        return workflow switch
        {
            GroupChatWorkflowDefinition groupChat => ResolveGroupChatSpeakerId(groupChat, assistantMessageIndex),
            SequentialWorkflowDefinition sequential => ResolveSequentialSpeakerId(sequential, assistantMessageIndex),
            AgentWorkflowDefinition handoff when assistantMessageIndex == 0 => handoff.StartAgentId,
            _ => null
        };
    }

    private static string? ResolveGroupChatSpeakerId(
        GroupChatWorkflowDefinition workflow,
        int assistantMessageIndex)
    {
        if (workflow.ParticipantAgentIds.Count == 0)
        {
            return null;
        }

        return workflow.Manager.Kind switch
        {
            GroupChatWorkflowManagerKind.RoundRobin or GroupChatWorkflowManagerKind.Programmable =>
                GroupChatManagerProgramResolver.ResolveSpeakerId(
                    workflow.Manager,
                    workflow.ParticipantAgentIds,
                    assistantMessageIndex),
            _ => null
        };
    }

    private static string? ResolveSequentialSpeakerId(
        SequentialWorkflowDefinition workflow,
        int assistantMessageIndex)
    {
        if (workflow.AgentOrder.Count == 0)
        {
            return null;
        }

        return assistantMessageIndex < workflow.AgentOrder.Count
            ? workflow.AgentOrder[assistantMessageIndex]
            : workflow.AgentOrder[^1];
    }
}
