using ChatClient.Shared.Models.StopAgents;

using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;

namespace ChatClient.Api.Client.Services;


internal class StopAgentFactory : IStopAgentFactory
{
    private readonly Dictionary<string, Func<IStopAgentOptions?, GroupChatManager>> _factories = new()
    {
        ["RoundRobin"] = o => CreateRoundRobin(o as RoundRobinStopAgentOptions),
        ["RoundRobinWithSummary"] = o => CreateRoundRobinWithSummary(o as RoundRobinSummaryStopAgentOptions)
    };

    public GroupChatManager Create(string name, IStopAgentOptions? options = null)
    {
        if (_factories.TryGetValue(name, out var factory))
            return factory(options);
        return CreateRoundRobin(options as RoundRobinStopAgentOptions);
    }

    private static GroupChatManager CreateRoundRobin(RoundRobinStopAgentOptions? opts)
    {
        var rounds = opts?.Rounds ?? 1;
        return new BridgingRoundRobinManager { MaximumInvocationCount = rounds };
    }

    private static GroupChatManager CreateRoundRobinWithSummary(RoundRobinSummaryStopAgentOptions? opts)
    {
        var rounds = opts?.Rounds ?? 1;
        var agent = opts?.SummaryAgent ?? string.Empty;

        if (string.IsNullOrWhiteSpace(agent))
        {
            throw new ArgumentException(
                "Summary agent is required for RoundRobinWithSummary strategy. " +
                "Please select a summary agent in the configuration.",
                nameof(opts));
        }

        return new RoundRobinSummaryGroupChatManager(agent) { MaximumInvocationCount = rounds };
    }
}

