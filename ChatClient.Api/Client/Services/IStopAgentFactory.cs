using ChatClient.Shared.Models.StopAgents;

using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;

namespace ChatClient.Api.Client.Services;

#pragma warning disable SKEXP0110
public interface IStopAgentFactory
{
    GroupChatManager Create(string name, IStopAgentOptions? options = null);
}
#pragma warning restore SKEXP0110
