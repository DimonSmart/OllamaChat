namespace ChatClient.Api.AgentWorkflows;

internal static class GroupChatWorkflowManagerValidator
{
    public static void Validate(
        GroupChatWorkflowManagerDefinition manager,
        IReadOnlyList<string> participantAgentIds)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(participantAgentIds);

        if (participantAgentIds.Count == 0)
        {
            throw new InvalidOperationException("Group chat requires at least one participant.");
        }

        if (manager.MaximumIterations <= 0)
        {
            throw new InvalidOperationException(
                "Group chat manager maximum iterations must be greater than zero.");
        }

        switch (manager.Kind)
        {
            case GroupChatWorkflowManagerKind.RoundRobin:
                return;

            case GroupChatWorkflowManagerKind.Custom:
                if (string.IsNullOrWhiteSpace(manager.ImplementationKey))
                {
                    throw new InvalidOperationException(
                        "Custom group chat managers require an implementation key.");
                }

                return;

            case GroupChatWorkflowManagerKind.Programmable:
                if (manager.Program is null)
                {
                    throw new InvalidOperationException(
                        "Programmable group chat managers require a program.");
                }

                ValidateProgrammableSchedule(manager, participantAgentIds);
                return;

            default:
                throw new InvalidOperationException(
                    $"Unsupported group chat manager kind '{manager.Kind}'.");
        }
    }

    private static void ValidateProgrammableSchedule(
        GroupChatWorkflowManagerDefinition manager,
        IReadOnlyList<string> participantAgentIds)
    {
        List<string> priorAssistantSpeakerIds = [];
        for (var turnIndex = 0; turnIndex < manager.MaximumIterations; turnIndex++)
        {
            try
            {
                priorAssistantSpeakerIds.Add(
                    GroupChatManagerProgramResolver.ResolveNextSpeakerId(
                        manager,
                        participantAgentIds,
                        priorAssistantSpeakerIds));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Programmable group chat manager failed to resolve turn {turnIndex + 1}: {ex.Message}",
                    ex);
            }
        }
    }
}
