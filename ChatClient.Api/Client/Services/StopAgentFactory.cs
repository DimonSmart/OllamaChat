using ChatClient.Shared.Models.StopAgents;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;

namespace ChatClient.Api.Client.Services;

#pragma warning disable SKEXP0110
internal class StopAgentFactory : IStopAgentFactory
{
    public GroupChatManager Create(string name, IStopAgentOptions? options = null) => name switch
    {
        "RoundRobin" => CreateRoundRobin(options as RoundRobinStopAgentOptions),
        _ => CreateRoundRobin(options as RoundRobinStopAgentOptions)
    };

    private static GroupChatManager CreateRoundRobin(RoundRobinStopAgentOptions? opts)
    {
        var rounds = opts?.Rounds ?? 1;
        return new RoundRobinGroupChatManager { MaximumInvocationCount = rounds };
    }
}
#pragma warning restore SKEXP0110
