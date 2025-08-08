using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0110

namespace ChatClient.Api.Client.Services;

internal sealed class ReasonableRoundRobinGroupChatManager(string stopAgentName, string stopPhrase) : RoundRobinGroupChatManager
{
    private readonly string _stopAgentName = stopAgentName;
    private readonly string _stopPhrase = stopPhrase;

    public override async ValueTask<GroupChatManagerResult<bool>> ShouldTerminate(
        ChatHistory history, CancellationToken cancellationToken = default)
    {
        var baseResult = await base.ShouldTerminate(history, cancellationToken);
        if (baseResult.Value)
        {
            return baseResult;
        }

        if (string.IsNullOrWhiteSpace(_stopAgentName) || string.IsNullOrWhiteSpace(_stopPhrase))
        {
            return baseResult;
        }

        var lastByAgent = history.LastOrDefault(m => m.AuthorName == _stopAgentName);
        if (lastByAgent?.ToString()?.Contains(_stopPhrase, StringComparison.OrdinalIgnoreCase) == true)
        {
            return new(true) { Reason = $"{_stopAgentName} said {_stopPhrase}" };
        }

        return baseResult;
    }
}
#pragma warning restore SKEXP0110
