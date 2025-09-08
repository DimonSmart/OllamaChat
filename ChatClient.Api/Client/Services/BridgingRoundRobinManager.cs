using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Client.Services;


public sealed class BridgingRoundRobinManager : ResettableRoundRobinGroupChatManager
{
    private ChatMessageContent? _fixedAgentMessage = null;
    private AuthorRole _fixedMessageRole = AuthorRole.Assistant;

    public override async ValueTask<GroupChatManagerResult<string>> SelectNextAgent(
        ChatHistory history,
        GroupChatTeam team,
        CancellationToken cancellationToken = default)
    {
        if (_fixedAgentMessage is not null)
        {
            _fixedAgentMessage.Role = _fixedMessageRole;
            _fixedAgentMessage = null;
        }

        GroupChatManagerResult<string> result = await base.SelectNextAgent(history, team, cancellationToken);


        ChatMessageContent? last = history.LastOrDefault();
        if (last is not null && last.Role == AuthorRole.Assistant)
        {
            _fixedAgentMessage = last;
            _fixedMessageRole = last.Role;
            last.Role = AuthorRole.User;
        }

        return result;
    }
}

