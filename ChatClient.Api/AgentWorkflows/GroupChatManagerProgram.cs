namespace ChatClient.Api.AgentWorkflows;

public sealed class GroupChatManagerProgram
{
    private readonly Func<GroupChatManagerProgramContext, string> _selectNextSpeaker;
    private readonly Func<WorkflowStartValues, int>? _maximumIterationsResolver;

    public GroupChatManagerProgram(
        Func<GroupChatManagerProgramContext, string> selectNextSpeaker,
        string? displayName = null)
        : this(selectNextSpeaker, displayName, maximumIterationsResolver: null)
    {
    }

    private GroupChatManagerProgram(
        Func<GroupChatManagerProgramContext, string> selectNextSpeaker,
        string? displayName,
        Func<WorkflowStartValues, int>? maximumIterationsResolver)
    {
        ArgumentNullException.ThrowIfNull(selectNextSpeaker);

        _selectNextSpeaker = selectNextSpeaker;
        _maximumIterationsResolver = maximumIterationsResolver;
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

    internal GroupChatManagerProgram WithMaximumIterationsResolver(
        Func<WorkflowStartValues, int> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return new GroupChatManagerProgram(_selectNextSpeaker, DisplayName, resolver);
    }

    internal GroupChatManagerProgram WithoutMaximumIterationsResolver() =>
        _maximumIterationsResolver is null
            ? this
            : new GroupChatManagerProgram(_selectNextSpeaker, DisplayName, maximumIterationsResolver: null);

    internal int ResolveMaximumIterations(WorkflowStartValues startValues, int fallback)
    {
        ArgumentNullException.ThrowIfNull(startValues);

        var maximumIterations = _maximumIterationsResolver?.Invoke(startValues) ?? fallback;
        if (maximumIterations <= 0)
        {
            throw new InvalidOperationException(
                "Resolved group chat maximum iterations must be greater than zero.");
        }

        return maximumIterations;
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
