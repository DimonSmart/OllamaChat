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
            AgentWorkflowDefinition handoff when assistantMessageIndex == 0 => handoff.StartParticipantId,
            _ => null
        };
    }

    private static string? ResolveGroupChatSpeakerId(
        GroupChatWorkflowDefinition workflow,
        int assistantMessageIndex)
    {
        if (workflow.ParticipantIds.Count == 0)
        {
            return null;
        }

        return workflow.Manager.Kind switch
        {
            GroupChatWorkflowManagerKind.RoundRobin or GroupChatWorkflowManagerKind.Programmable =>
                GroupChatManagerProgramResolver.ResolveSpeakerId(
                    workflow.Manager,
                    workflow.ParticipantIds,
                    assistantMessageIndex),
            _ => null
        };
    }

    private static string? ResolveSequentialSpeakerId(
        SequentialWorkflowDefinition workflow,
        int assistantMessageIndex)
    {
        if (workflow.ParticipantOrder.Count == 0)
        {
            return null;
        }

        return assistantMessageIndex < workflow.ParticipantOrder.Count
            ? workflow.ParticipantOrder[assistantMessageIndex]
            : workflow.ParticipantOrder[^1];
    }
}
