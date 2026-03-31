namespace ChatClient.Api.AgentWorkflows;

public sealed class SequentialConfigurationBuilder
{
    private readonly List<string> _agentOrder = [];

    public SequentialConfigurationBuilder Order(params string[] agentIds)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        _agentOrder.Clear();

        foreach (var agentId in agentIds)
        {
            AddAgent(agentId);
        }

        return this;
    }

    public SequentialConfigurationBuilder Then(string agentId)
    {
        AddAgent(agentId);
        return this;
    }

    internal IReadOnlyList<string> AgentOrder => _agentOrder;

    private void AddAgent(string agentId)
    {
        var normalizedAgentId = RequireValue(agentId, nameof(agentId));
        _agentOrder.RemoveAll(existing =>
            string.Equals(existing, normalizedAgentId, StringComparison.OrdinalIgnoreCase));
        _agentOrder.Add(normalizedAgentId);
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
