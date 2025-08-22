using System.Collections.Generic;

using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Client.Services;

#pragma warning disable SKEXP0110
public sealed class RoundRobinSummaryGroupChatManager(string summaryAgentName) : RoundRobinGroupChatManager, IGroupChatAgentProvider
{
    private readonly string _summaryAgentName = summaryAgentName;
    private bool _summaryPending;
    private bool _summaryDone;

    public IEnumerable<string> GetRequiredAgents()
    {
        if (!string.IsNullOrEmpty(_summaryAgentName))
            yield return _summaryAgentName;
    }

    public override async ValueTask<GroupChatManagerResult<bool>> ShouldTerminate(
        ChatHistory history,
        CancellationToken cancellationToken = default)
    {
        var baseResult = await base.ShouldTerminate(history, cancellationToken);
        if (_summaryDone)
            return baseResult;
        if (!baseResult.Value)
            return baseResult;
        if (!_summaryPending)
        {
            _summaryPending = true;
            return new(false);
        }
        return new(false);
    }

    public override async ValueTask<GroupChatManagerResult<string>> SelectNextAgent(
        ChatHistory history,
        GroupChatTeam team,
        CancellationToken cancellationToken = default)
    {
        if (_summaryPending && !_summaryDone)
        {
            _summaryDone = true;
            _summaryPending = false;
            return new(_summaryAgentName);
        }

        GroupChatManagerResult<string> result;
        do
        {
            result = await base.SelectNextAgent(history, team, cancellationToken);
        }
        while (result.Value == _summaryAgentName && team.Count > 1);

        return result;
    }
}
#pragma warning restore SKEXP0110
