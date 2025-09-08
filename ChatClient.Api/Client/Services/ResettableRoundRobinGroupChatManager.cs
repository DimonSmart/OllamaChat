using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Client.Services;

public class ResettableRoundRobinGroupChatManager : GroupChatManager
{
    private int _currentAgentIndex;
    private int _invocationCount;
    private int _maximumInvocationCount = 1;

    public new int MaximumInvocationCount
    {
        get => _maximumInvocationCount;
        set => _maximumInvocationCount = value;
    }

    public void ResetInvocationCount()
    {
        _invocationCount = 0;
        _currentAgentIndex = 0;
    }

    public override ValueTask<GroupChatManagerResult<bool>> ShouldTerminate(
        ChatHistory history,
        CancellationToken cancellationToken = default)
    {
        var terminate = _invocationCount >= _maximumInvocationCount;
        return ValueTask.FromResult(new GroupChatManagerResult<bool>(terminate)
        {
            Reason = terminate ? "Maximum invocation count reached." : "Additional turns available."
        });
    }

    public override ValueTask<GroupChatManagerResult<bool>> ShouldRequestUserInput(
        ChatHistory history,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(new GroupChatManagerResult<bool>(false)
        {
            Reason = "Round-robin manager does not request user input."
        });
    }

    public override ValueTask<GroupChatManagerResult<string>> FilterResults(
        ChatHistory history,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(new GroupChatManagerResult<string>(history.LastOrDefault()?.Content ?? string.Empty)
        {
            Reason = "Default result filter provides the final chat message."
        });
    }

    public override ValueTask<GroupChatManagerResult<string>> SelectNextAgent(
        ChatHistory history,
        GroupChatTeam team,
        CancellationToken cancellationToken = default)
    {
        var key = team.Skip(_currentAgentIndex).First().Key;
        _currentAgentIndex = (_currentAgentIndex + 1) % team.Count;
        _invocationCount++;
        return ValueTask.FromResult(new GroupChatManagerResult<string>(key)
        {
            Reason = $"Selected agent at index: {_currentAgentIndex}"
        });
    }
}
