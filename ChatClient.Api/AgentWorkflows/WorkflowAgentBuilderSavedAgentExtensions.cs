using ChatClient.Application.Services.AgentRuntime;

namespace ChatClient.Api.AgentWorkflows;

public static class WorkflowAgentBuilderSavedAgentExtensions
{
    public static WorkflowAgentBuilder UseAgentById(
        this WorkflowAgentBuilder builder,
        Guid agentId)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.UseDefinition(new AgentDefinitionReference(
            AgentDefinitionKind.SavedAgent,
            agentId.ToString("D")));
    }

    public static WorkflowAgentBuilder UseAgentById(
        this WorkflowAgentBuilder builder,
        string agentId)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!Guid.TryParse(RequireValue(agentId, nameof(agentId)), out var parsedAgentId))
        {
            throw new ArgumentException("Saved agent id must be a valid GUID.", nameof(agentId));
        }

        return builder.UseAgentById(parsedAgentId);
    }

    private static string RequireValue(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", paramName);
        }

        return value.Trim();
    }
}
