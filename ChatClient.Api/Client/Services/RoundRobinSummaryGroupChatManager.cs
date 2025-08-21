using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Client.Services;

#pragma warning disable SKEXP0110
public sealed class RoundRobinSummaryGroupChatManager(string summaryAgentName) : RoundRobinGroupChatManager
{
    private readonly string _summaryAgentName = summaryAgentName;
    private bool _summaryPending;
    private bool _summaryDone;

    public override async ValueTask<GroupChatManagerResult<bool>> ShouldTerminate(
        ChatHistory history,
        CancellationToken cancellationToken = default)
    {
        var baseResult = await base.ShouldTerminate(history, cancellationToken);
        if (_summaryDone) return baseResult;
        if (!baseResult.Value) return baseResult;
        if (!_summaryPending)
        {
            _summaryPending = true;
            return new(false);
        }
        return new(false);
    }

    public override ValueTask<GroupChatManagerResult<string>> SelectNextAgent(
        ChatHistory history,
        GroupChatTeam team,
        CancellationToken cancellationToken = default)
    {
        if (_summaryPending && !_summaryDone)
        {
            _summaryDone = true;
            _summaryPending = false;
            return ValueTask.FromResult(new GroupChatManagerResult<string>(_summaryAgentName));
        }
        return base.SelectNextAgent(history, team, cancellationToken);
    }
}
#pragma warning restore SKEXP0110
