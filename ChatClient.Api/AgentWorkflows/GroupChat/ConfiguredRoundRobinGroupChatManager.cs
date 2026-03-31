using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.AgentWorkflows.GroupChat;

public sealed class ConfiguredRoundRobinGroupChatManager : GroupChatManager
{
    private readonly IReadOnlyList<AIAgent> _agents;

    public ConfiguredRoundRobinGroupChatManager(
        IReadOnlyList<AIAgent> agents,
        int maximumIterations)
    {
        ArgumentNullException.ThrowIfNull(agents);
        if (agents.Count == 0)
        {
            throw new InvalidOperationException("Group chat requires at least one participant.");
        }

        if (maximumIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumIterations),
                maximumIterations,
                "Maximum iterations must be greater than zero.");
        }

        _agents = agents;
        MaximumIterationCount = maximumIterations;
    }

    protected override ValueTask<AIAgent> SelectNextAgentAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        var assistantMessageCount = CountAssistantMessages(history);
        var nextAgent = _agents[assistantMessageCount % _agents.Count];
        return ValueTask.FromResult(nextAgent);
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

    private static int CountAssistantMessages(IReadOnlyList<ChatMessage> history) =>
        history.Count(static message => message.Role == ChatRole.Assistant);
}
