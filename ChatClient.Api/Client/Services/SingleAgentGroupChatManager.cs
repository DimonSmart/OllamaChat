using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0110

namespace ChatClient.Api.Client.Services;

/// <summary>
/// Group chat manager for single agent scenarios - always terminates after first response
/// </summary>
internal sealed class SingleAgentGroupChatManager : RoundRobinGroupChatManager
{
    public override ValueTask<GroupChatManagerResult<string>> SelectNextAgent(
        ChatHistory history,
        GroupChatTeam team,
        CancellationToken cancellationToken = default)
    {
        // Use base implementation to get first agent
        return base.SelectNextAgent(history, team, cancellationToken);
    }

    public override ValueTask<GroupChatManagerResult<bool>> ShouldTerminate(
        ChatHistory history,
        CancellationToken cancellationToken = default)
    {
        if (InvocationCount > 0)
        {
            return new ValueTask<GroupChatManagerResult<bool>>(
                new GroupChatManagerResult<bool>(true)
                {
                    Reason = "Single agent completed response"
                });
        }

        return new ValueTask<GroupChatManagerResult<bool>>(
            new GroupChatManagerResult<bool>(false));
    }
}

#pragma warning restore SKEXP0110
