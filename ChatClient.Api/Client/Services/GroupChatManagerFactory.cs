using ChatClient.Domain.Models.ChatStrategies;

using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;

namespace ChatClient.Api.Client.Services;


internal class GroupChatManagerFactory : IGroupChatManagerFactory
{
    public GroupChatManager Create(string name, IChatStrategyOptions? options = null)
    {
        return name switch
        {
            "RoundRobin" => new BridgingRoundRobinManager
            {
                MaximumInvocationCount = (options as RoundRobinChatStrategyOptions)?.Rounds ?? 1
            },

            "RoundRobinWithSummary" => CreateRoundRobinWithSummary(options as RoundRobinSummaryChatStrategyOptions),

            _ => new BridgingRoundRobinManager { MaximumInvocationCount = 1 }
        };
    }

    private static GroupChatManager CreateRoundRobinWithSummary(RoundRobinSummaryChatStrategyOptions? opts)
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

