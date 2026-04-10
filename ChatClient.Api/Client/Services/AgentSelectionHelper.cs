using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Services;

public static class AgentSelectionHelper
{
    public static AgentTemplateDefinition? FindByName(
        IEnumerable<AgentTemplateDefinition> agents,
        string? agentName)
    {
        if (string.IsNullOrWhiteSpace(agentName))
            return null;

        return agents.FirstOrDefault(agent =>
            string.Equals(agent.AgentName, agentName, StringComparison.OrdinalIgnoreCase));
    }
}
