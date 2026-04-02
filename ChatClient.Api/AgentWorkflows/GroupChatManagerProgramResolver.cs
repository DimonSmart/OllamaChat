namespace ChatClient.Api.AgentWorkflows;

internal static class GroupChatManagerProgramResolver
{
    public static string? ResolveSpeakerId(
        GroupChatWorkflowManagerDefinition manager,
        IReadOnlyList<string> participantAgentIds,
        int assistantMessageIndex)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(participantAgentIds);

        if (assistantMessageIndex < 0 || assistantMessageIndex >= manager.MaximumIterations)
        {
            return null;
        }

        if (manager.Kind == GroupChatWorkflowManagerKind.RoundRobin)
        {
            return participantAgentIds.Count == 0
                ? null
                : participantAgentIds[assistantMessageIndex % participantAgentIds.Count];
        }

        if (manager.Kind != GroupChatWorkflowManagerKind.Programmable)
        {
            return null;
        }

        List<string> priorAssistantSpeakerIds = [];
        while (priorAssistantSpeakerIds.Count < assistantMessageIndex)
        {
            priorAssistantSpeakerIds.Add(ResolveNextSpeakerId(manager, participantAgentIds, priorAssistantSpeakerIds));
        }

        return ResolveNextSpeakerId(manager, participantAgentIds, priorAssistantSpeakerIds);
    }

    public static string ResolveNextSpeakerId(
        GroupChatWorkflowManagerDefinition manager,
        IReadOnlyList<string> participantAgentIds,
        IReadOnlyList<string> priorAssistantSpeakerIds)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(participantAgentIds);
        ArgumentNullException.ThrowIfNull(priorAssistantSpeakerIds);

        if (participantAgentIds.Count == 0)
        {
            throw new InvalidOperationException("Group chat requires at least one participant.");
        }

        if (manager.MaximumIterations <= 0)
        {
            throw new InvalidOperationException(
                "Group chat manager maximum iterations must be greater than zero.");
        }

        if (priorAssistantSpeakerIds.Count >= manager.MaximumIterations)
        {
            throw new InvalidOperationException(
                "Group chat manager has no remaining turns.");
        }

        return manager.Kind switch
        {
            GroupChatWorkflowManagerKind.RoundRobin =>
                participantAgentIds[priorAssistantSpeakerIds.Count % participantAgentIds.Count],
            GroupChatWorkflowManagerKind.Programmable =>
                ResolveProgrammableSpeakerId(manager, participantAgentIds, priorAssistantSpeakerIds),
            _ => throw new InvalidOperationException(
                $"Group chat manager kind '{manager.Kind}' does not support workflow-defined speaker resolution.")
        };
    }

    private static string ResolveProgrammableSpeakerId(
        GroupChatWorkflowManagerDefinition manager,
        IReadOnlyList<string> participantAgentIds,
        IReadOnlyList<string> priorAssistantSpeakerIds)
    {
        var program = manager.Program
            ?? throw new InvalidOperationException(
                "Programmable group chat managers require a program.");

        var context = new GroupChatManagerProgramContext(
            participantAgentIds,
            priorAssistantSpeakerIds,
            priorAssistantSpeakerIds.Count,
            manager.MaximumIterations);
        var speakerId = program.SelectNextSpeaker(context);
        var match = participantAgentIds.FirstOrDefault(participantId =>
            string.Equals(participantId, speakerId, StringComparison.OrdinalIgnoreCase));

        return match ?? throw new InvalidOperationException(
            $"Programmable group chat manager returned unknown speaker '{speakerId}'.");
    }
}
