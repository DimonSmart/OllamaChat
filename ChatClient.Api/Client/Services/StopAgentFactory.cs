using ChatClient.Shared.Models.StopAgents;

using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;

namespace ChatClient.Api.Client.Services;


internal class StopAgentFactory : IStopAgentFactory
{
    public GroupChatManager Create(string name, IStopAgentOptions? options = null)
    {
        return name switch
        {
            "RoundRobin" => new BridgingRoundRobinManager
            {
                MaximumInvocationCount = (options as RoundRobinStopAgentOptions)?.Rounds ?? 1
            },

            "RoundRobinWithSummary" => CreateRoundRobinWithSummary(options as RoundRobinSummaryStopAgentOptions),

            _ => new BridgingRoundRobinManager { MaximumInvocationCount = 1 }
        };
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

