namespace ChatClient.Api.AgentWorkflows;

public sealed class GroupChatManagerProgram
{
    private readonly Func<GroupChatManagerProgramContext, string> _selectNextSpeaker;

    public GroupChatManagerProgram(
        Func<GroupChatManagerProgramContext, string> selectNextSpeaker,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(selectNextSpeaker);

        _selectNextSpeaker = selectNextSpeaker;
        DisplayName = NormalizeOptional(displayName);
    }

    public string? DisplayName { get; }

    public string SelectNextSpeaker(GroupChatManagerProgramContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var speakerId = _selectNextSpeaker(context);
        if (string.IsNullOrWhiteSpace(speakerId))
        {
            throw new InvalidOperationException(
                "Group chat manager program must return a non-empty speaker id.");
        }

        return speakerId.Trim();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class GroupChatManagerProgramContext
{
    public GroupChatManagerProgramContext(
        IReadOnlyList<string> participantAgentIds,
        IReadOnlyList<string> priorAssistantSpeakerIds,
        int assistantMessageIndex,
        int maximumIterations)
    {
        ArgumentNullException.ThrowIfNull(participantAgentIds);
        ArgumentNullException.ThrowIfNull(priorAssistantSpeakerIds);

        if (assistantMessageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(assistantMessageIndex));
        }

        if (maximumIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumIterations));
        }

        ParticipantAgentIds = participantAgentIds.ToArray();
        PriorAssistantSpeakerIds = priorAssistantSpeakerIds.ToArray();
        AssistantMessageIndex = assistantMessageIndex;
        MaximumIterations = maximumIterations;
    }

    public IReadOnlyList<string> ParticipantAgentIds { get; }

    public IReadOnlyList<string> PriorAssistantSpeakerIds { get; }

    public int AssistantMessageIndex { get; }

    public int MaximumIterations { get; }

    public string? LastSpeakerId => PriorAssistantSpeakerIds.Count == 0
        ? null
        : PriorAssistantSpeakerIds[^1];
}
