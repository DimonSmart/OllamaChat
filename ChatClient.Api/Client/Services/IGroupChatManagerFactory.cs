using ChatClient.Domain.Models.ChatStrategies;

using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;

namespace ChatClient.Api.Client.Services;


public interface IGroupChatManagerFactory
{
    GroupChatManager Create(string name, IChatStrategyOptions? options = null);
}

