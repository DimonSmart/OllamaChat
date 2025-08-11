using System.Threading;
using System.Threading.Tasks;

using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0110

namespace ChatClient.Api.Client.Services;

internal interface IGroupChatStrategy
{
    ValueTask<GroupChatManagerResult<string>> SelectNextAgent(
        ChatHistory history,
        GroupChatTeam team,
        CancellationToken cancellationToken = default);

    ValueTask<GroupChatManagerResult<bool>> ShouldTerminate(
        ChatHistory history,
        CancellationToken cancellationToken = default);
}

#pragma warning restore SKEXP0110
