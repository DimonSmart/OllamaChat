using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Client.Services;

#pragma warning disable SKEXP0110
public sealed class BridgingRoundRobinManager : RoundRobinGroupChatManager
{
    private ChatMessageContent? _fixedAgentMessage = null;
    private AuthorRole _fixedMessageRole = AuthorRole.Assistant;

    public override ValueTask<GroupChatManagerResult<string>> SelectNextAgent(
        ChatHistory history,
        GroupChatTeam team,
        CancellationToken cancellationToken = default)
    {
        if (_fixedAgentMessage != null)
        {
            _fixedAgentMessage.Role = _fixedMessageRole;
        }

        ChatMessageContent? last = history.LastOrDefault();
        if (last is not null
            && last.Role == AuthorRole.Assistant)
        {
            _fixedAgentMessage = last;
            _fixedMessageRole = last.Role;

            last.Role = AuthorRole.User;
        }

        return base.SelectNextAgent(history, team, cancellationToken);
    }
}
#pragma warning restore SKEXP0110
