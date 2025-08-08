using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0110

namespace ChatClient.Api.Client.Services;

internal sealed class RandomGroupChatStrategy : IGroupChatStrategy
{
    public ValueTask<GroupChatManagerResult<string>> SelectNextAgent(
        ChatHistory history,
        GroupChatTeam team,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public ValueTask<GroupChatManagerResult<bool>> ShouldTerminate(
        ChatHistory history,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}

#pragma warning restore SKEXP0110
