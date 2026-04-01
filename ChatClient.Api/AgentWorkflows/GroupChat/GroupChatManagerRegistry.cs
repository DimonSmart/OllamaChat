using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace ChatClient.Api.AgentWorkflows.GroupChat;

public sealed class GroupChatManagerRegistry(
    IEnumerable<IGroupChatManagerFactory> factories)
{
    private readonly Dictionary<string, IGroupChatManagerFactory> _factories = factories.ToDictionary(
        static factory => factory.Key,
        StringComparer.OrdinalIgnoreCase);

    public GroupChatManager Create(
        string key,
        IReadOnlyList<AIAgent> agents,
        GroupChatWorkflowDefinition workflow,
        IReadOnlyList<string> priorAssistantSpeakerIds)
    {
        if (!_factories.TryGetValue(key, out var factory))
        {
            throw new InvalidOperationException(
                $"Group chat manager '{key}' is not registered.");
        }

        return factory.Create(agents, workflow, priorAssistantSpeakerIds);
    }
}
