using ChatClient.Shared.Models.StopAgents;

using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;

namespace ChatClient.Api.Client.Services;


public interface IStopAgentFactory
{
    GroupChatManager Create(string name, IStopAgentOptions? options = null);
}

