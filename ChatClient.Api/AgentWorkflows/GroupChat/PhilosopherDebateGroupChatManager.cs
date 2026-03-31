using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.AgentWorkflows.GroupChat;

public sealed class PhilosopherDebateGroupChatManager : GroupChatManager
{
    private readonly AIAgent _host;
    private readonly AIAgent _kant;
    private readonly AIAgent _nietzsche;
    private readonly AIAgent _judge;

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

        _host = FindRequiredAgent(agents, "host");
        _kant = FindRequiredAgent(agents, "kant");
        _nietzsche = FindRequiredAgent(agents, "nietzsche");
        _judge = FindRequiredAgent(agents, "judge");
        MaximumIterationCount = maximumIterations;
    }

    protected override ValueTask<AIAgent> SelectNextAgentAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        var assistantMessageCount = CountAssistantMessages(history);

        return ValueTask.FromResult(assistantMessageCount switch
        {
            0 => _host,
            1 => _kant,
            2 => _nietzsche,
            var index when index == MaximumIterationCount - 3 => _kant,
            var index when index == MaximumIterationCount - 2 => _nietzsche,
            var index when index == MaximumIterationCount - 1 => _judge,
            var index when index < MaximumIterationCount - 3 => index % 2 == 1 ? _kant : _nietzsche,
            _ => _judge
        });
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
