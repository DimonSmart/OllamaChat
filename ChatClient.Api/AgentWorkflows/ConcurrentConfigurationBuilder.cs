namespace ChatClient.Api.AgentWorkflows;

public sealed class ConcurrentConfigurationBuilder
{
    private readonly List<string> _participantAgentIds = [];
    private ConcurrentWorkflowAggregationDefinition _aggregation = new();

    public ConcurrentConfigurationBuilder Participant(string agentId)
    {
        var normalizedAgentId = RequireValue(agentId, nameof(agentId));
        _participantAgentIds.RemoveAll(existing =>
            string.Equals(existing, normalizedAgentId, StringComparison.OrdinalIgnoreCase));
        _participantAgentIds.Add(normalizedAgentId);
        return this;
    }

    public ConcurrentConfigurationBuilder Participants(params string[] agentIds)
    {
        ArgumentNullException.ThrowIfNull(agentIds);

        foreach (var agentId in agentIds)
        {
            Participant(agentId);
        }

        return this;
    }

    public ConcurrentConfigurationBuilder AggregateLastMessagePerAgent()
    {
        _aggregation = new ConcurrentWorkflowAggregationDefinition
        {
            Kind = ConcurrentWorkflowAggregationKind.LastMessagePerAgent
        };
        return this;
    }

    public ConcurrentConfigurationBuilder ConcatenateAllMessages()
    {
        _aggregation = new ConcurrentWorkflowAggregationDefinition
        {
            Kind = ConcurrentWorkflowAggregationKind.ConcatenateAllMessages
        };
        return this;
    }

    internal IReadOnlyList<string> ParticipantAgentIds => _participantAgentIds;

    internal ConcurrentWorkflowAggregationDefinition Aggregation => _aggregation;

    private static string RequireValue(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", paramName);
        }

        return value.Trim();
    }
}
