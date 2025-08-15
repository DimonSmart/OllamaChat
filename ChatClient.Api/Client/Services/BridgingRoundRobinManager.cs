using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Client.Services;

#pragma warning disable SKEXP0110
public sealed class BridgingRoundRobinManager : RoundRobinGroupChatManager
{
    private readonly HashSet<ChatMessageContent> _bridgedMessages = new();

    public override ValueTask<GroupChatManagerResult<string>> SelectNextAgent(
        ChatHistory history,
        GroupChatTeam team,
        CancellationToken cancellationToken = default)
    {
        var last = history.LastOrDefault();
        if (last is not null
            && last.Role == AuthorRole.Assistant
            && !_bridgedMessages.Contains(last))
        {
            var text = string.Join("", last.Items.OfType<TextContent>().Select(t => t.Text));
            if (!string.IsNullOrWhiteSpace(text))
            {
                history.AddUserMessage(text);
                _bridgedMessages.Add(last);
            }
        }

        return base.SelectNextAgent(history, team, cancellationToken);
    }
}
#pragma warning restore SKEXP0110
