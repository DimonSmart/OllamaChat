using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.AgentWorkflows.GroupChat;

public sealed class PhilosopherDebateGroupChatManager : GroupChatManager
{
    private readonly IReadOnlyDictionary<string, AIAgent> _agentsBySpeakerId;

    public PhilosopherDebateGroupChatManager(
        IReadOnlyList<AIAgent> agents,
        int maximumIterations)
    {
        ArgumentNullException.ThrowIfNull(agents);
        if (maximumIterations < 8)
        {
            throw new InvalidOperationException(
                "Philosopher debate group chat requires at least 8 iterations.");
        }

        _agentsBySpeakerId = new Dictionary<string, AIAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = FindRequiredAgent(agents, "host"),
            ["kant"] = FindRequiredAgent(agents, "kant"),
            ["nietzsche"] = FindRequiredAgent(agents, "nietzsche"),
            ["judge"] = FindRequiredAgent(agents, "judge")
        };
        MaximumIterationCount = maximumIterations;
    }

    protected override ValueTask<AIAgent> SelectNextAgentAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        var assistantMessageCount = CountAssistantMessages(history);
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
        var assistantMessageCount = CountAssistantMessages(history);
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
