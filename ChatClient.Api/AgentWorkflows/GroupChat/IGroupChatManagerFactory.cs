using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace ChatClient.Api.AgentWorkflows.GroupChat;

public interface IGroupChatManagerFactory
{
    string Key { get; }

    GroupChatManager Create(
        IReadOnlyList<AIAgent> agents,
        GroupChatWorkflowDefinition workflow);
}
