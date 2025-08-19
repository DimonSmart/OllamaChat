using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Client.Services;

#pragma warning disable SKEXP0110
public sealed class BridgingRoundRobinManager : RoundRobinGroupChatManager
{
    private ChatMessageContent? _fixedAgentMessage = null;
    private AuthorRole _fixedMessageRole = AuthorRole.Assistant;

    public override async ValueTask<GroupChatManagerResult<string>> SelectNextAgent(
        ChatHistory history,
        GroupChatTeam team,
        CancellationToken cancellationToken = default)
    {
        // 0) Откат предыдущей временной подмены роли (если была)
        if (_fixedAgentMessage is not null)
        {
            _fixedAgentMessage.Role = _fixedMessageRole;
            _fixedAgentMessage = null;
        }

        // 1) Кто следующий — пусть решит базовый round-robin
        GroupChatManagerResult<string> result = await base.SelectNextAgent(history, team, cancellationToken);


        // 3) Подменяем роль последнего сообщения на User перед вызовом LLM
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
#pragma warning restore SKEXP0110
