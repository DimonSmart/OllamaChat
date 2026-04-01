using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace ChatClient.Api.AgentWorkflows.GroupChat;

public sealed class PhilosopherDebateGroupChatManagerFactory : IGroupChatManagerFactory
{
    public string Key => "philosopher-debate";

    public GroupChatManager Create(
        IReadOnlyList<AIAgent> agents,
        GroupChatWorkflowDefinition workflow,
        IReadOnlyList<string> priorAssistantSpeakerIds)
    {
        return new PhilosopherDebateGroupChatManager(
            agents,
            workflow.Manager.MaximumIterations,
            priorAssistantSpeakerIds.Count);
    }
}
