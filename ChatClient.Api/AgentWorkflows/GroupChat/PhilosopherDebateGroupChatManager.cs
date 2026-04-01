using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.AgentWorkflows.GroupChat;

public sealed class PhilosopherDebateGroupChatManager : GroupChatManager
{
    private readonly IReadOnlyDictionary<string, AIAgent> _agentsBySpeakerId;
    private readonly int _assistantMessagesBeforeRun;

    public PhilosopherDebateGroupChatManager(
        IReadOnlyList<AIAgent> agents,
        int maximumIterations,
        int assistantMessagesBeforeRun = 0)
    {
        ArgumentNullException.ThrowIfNull(agents);
        if (maximumIterations < 8)
        {
            throw new InvalidOperationException(
                "Philosopher debate group chat requires at least 8 iterations.");
        }

        if (assistantMessagesBeforeRun < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(assistantMessagesBeforeRun));
        }

        _agentsBySpeakerId = new Dictionary<string, AIAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = FindRequiredAgent(agents, "host"),
            ["kant"] = FindRequiredAgent(agents, "kant"),
            ["nietzsche"] = FindRequiredAgent(agents, "nietzsche"),
            ["judge"] = FindRequiredAgent(agents, "judge")
        };
        _assistantMessagesBeforeRun = assistantMessagesBeforeRun;
        MaximumIterationCount = maximumIterations;
    }

    internal int AssistantMessagesBeforeRun => _assistantMessagesBeforeRun;

    protected override ValueTask<AIAgent> SelectNextAgentAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        var assistantMessageCount = _assistantMessagesBeforeRun + CountAssistantMessages(history);
        var speakerId = PhilosopherDebateTurnSchedule.ResolveSpeakerId(
            assistantMessageCount,
            MaximumIterationCount);

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
        var assistantMessageCount = _assistantMessagesBeforeRun + CountAssistantMessages(history);
        return ValueTask.FromResult(assistantMessageCount >= MaximumIterationCount);
    }

    protected override void Reset()
    {
    }

    private static AIAgent FindRequiredAgent(
        IReadOnlyList<AIAgent> agents,
        string token)
    {
        var match = agents.FirstOrDefault(agent =>
            string.Equals(agent.Id, token, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(agent.Name) &&
             agent.Name.Contains(token, StringComparison.OrdinalIgnoreCase)));

        return match ?? throw new InvalidOperationException(
            $"Philosopher debate group chat requires participant '{token}'.");
    }

    private static int CountAssistantMessages(IReadOnlyList<ChatMessage> history) =>
        history.Count(static message => message.Role == ChatRole.Assistant);
}
