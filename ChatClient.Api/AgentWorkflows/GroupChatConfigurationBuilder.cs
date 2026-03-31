namespace ChatClient.Api.AgentWorkflows;

public sealed class GroupChatConfigurationBuilder
{
    private readonly List<string> _participantAgentIds = [];
    private GroupChatWorkflowManagerDefinition _manager = new();

    public GroupChatConfigurationBuilder Participant(string agentId)
    {
        var normalizedAgentId = RequireValue(agentId, nameof(agentId));
        _participantAgentIds.RemoveAll(existing =>
            string.Equals(existing, normalizedAgentId, StringComparison.OrdinalIgnoreCase));
        _participantAgentIds.Add(normalizedAgentId);
        return this;
    }

    public GroupChatConfigurationBuilder Participants(params string[] agentIds)
    {
        ArgumentNullException.ThrowIfNull(agentIds);

        foreach (var agentId in agentIds)
        {
            Participant(agentId);
        }

        return this;
    }

    public GroupChatConfigurationBuilder UseRoundRobinManager(int maximumIterations = 40)
    {
        if (maximumIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumIterations),
                maximumIterations,
                "Maximum iterations must be greater than zero.");
        }

        _manager = new GroupChatWorkflowManagerDefinition
        {
            Kind = GroupChatWorkflowManagerKind.RoundRobin,
            MaximumIterations = maximumIterations
        };
        return this;
    }

    public GroupChatConfigurationBuilder UseCustomManager(
        string implementationKey,
        int maximumIterations = 40)
    {
        if (maximumIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumIterations),
                maximumIterations,
                "Maximum iterations must be greater than zero.");
        }

        _manager = new GroupChatWorkflowManagerDefinition
        {
            Kind = GroupChatWorkflowManagerKind.Custom,
            MaximumIterations = maximumIterations,
            ImplementationKey = RequireValue(implementationKey, nameof(implementationKey))
        };
        return this;
    }

    internal IReadOnlyList<string> ParticipantAgentIds => _participantAgentIds;

    internal GroupChatWorkflowManagerDefinition Manager => _manager;

    private static string RequireValue(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", paramName);
        }

        return value.Trim();
    }
}
