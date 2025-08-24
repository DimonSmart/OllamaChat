using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;



namespace ChatClient.Api.Client.Services;

internal sealed class ReasonableRoundRobinGroupChatManager(
    string stopAgentName,
    string stopPhrase) : RoundRobinGroupChatManager
{
    private readonly StopPhraseEvaluator _stopEvaluator = new(stopAgentName, stopPhrase);

    public override async ValueTask<GroupChatManagerResult<bool>> ShouldTerminate(
        ChatHistory history,
        CancellationToken cancellationToken = default)
    {
        var baseResult = await base.ShouldTerminate(history, cancellationToken);
        if (baseResult.Value)
        {
            return baseResult;
        }

        if (_stopEvaluator.Evaluate(history, out var stopResult))
        {
            return stopResult;
        }

        return baseResult;
    }
}

