using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.AgentWorkflows.GroupChat;

public sealed class ConfiguredProgrammableGroupChatManager : GroupChatManager
{
    private readonly IReadOnlyDictionary<string, AIAgent> _agentsBySpeakerId;
    private readonly IReadOnlyList<string> _participantAgentIds;
    private readonly GroupChatWorkflowManagerDefinition _manager;
    private readonly IReadOnlyList<string> _priorAssistantSpeakerIds;

    public ConfiguredProgrammableGroupChatManager(
        IReadOnlyList<AIAgent> agents,
        IReadOnlyList<string> participantAgentIds,
        GroupChatWorkflowManagerDefinition manager,
        IReadOnlyList<string> priorAssistantSpeakerIds)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(participantAgentIds);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(priorAssistantSpeakerIds);

        if (agents.Count != participantAgentIds.Count)
        {
            throw new InvalidOperationException(
                "Programmable group chat manager requires one runtime agent per participant id.");
        }

        _participantAgentIds = participantAgentIds.ToArray();
        _manager = manager;
        _priorAssistantSpeakerIds = priorAssistantSpeakerIds.ToArray();
        _agentsBySpeakerId = participantAgentIds
            .Select((participantId, index) => new KeyValuePair<string, AIAgent>(participantId, agents[index]))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        MaximumIterationCount = manager.MaximumIterations;
    }

    internal int AssistantMessagesBeforeRun => _priorAssistantSpeakerIds.Count;

    protected override ValueTask<AIAgent> SelectNextAgentAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        var currentRunAssistantMessageCount = CountAssistantMessages(history);
        var priorAssistantSpeakerIds = _priorAssistantSpeakerIds
            .Concat(GetCurrentRunSpeakerIds(currentRunAssistantMessageCount))
            .ToArray();
        var speakerId = GroupChatManagerProgramResolver.ResolveNextSpeakerId(
            _manager,
            _participantAgentIds,
            priorAssistantSpeakerIds);

        return ValueTask.FromResult(_agentsBySpeakerId[speakerId]);
    }

    protected override ValueTask<IEnumerable<ChatMessage>> UpdateHistoryAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IEnumerable<ChatMessage>>(history);

    protected override ValueTask<bool> ShouldTerminateAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        var assistantMessageCount = _priorAssistantSpeakerIds.Count + CountAssistantMessages(history);
        return ValueTask.FromResult(assistantMessageCount >= MaximumIterationCount);
    }

    protected override void Reset()
    {
    }

    private IEnumerable<string> GetCurrentRunSpeakerIds(int currentRunAssistantMessageCount)
    {
        List<string> speakerIds = [];
        while (speakerIds.Count < currentRunAssistantMessageCount)
        {
            var priorAssistantSpeakerIds = _priorAssistantSpeakerIds
                .Concat(speakerIds)
                .ToArray();
            speakerIds.Add(GroupChatManagerProgramResolver.ResolveNextSpeakerId(
                _manager,
                _participantAgentIds,
                priorAssistantSpeakerIds));
        }

        return speakerIds;
    }

    private static int CountAssistantMessages(IReadOnlyList<ChatMessage> history) =>
        history.Count(static message => message.Role == ChatRole.Assistant);
}
