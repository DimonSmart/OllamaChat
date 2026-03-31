namespace ChatClient.Api.AgentWorkflows;

public sealed class HandoffConfigurationBuilder
{
    private string? _startAgentId;
    private readonly List<AgentWorkflowHandoffDefinition> _handoffs = [];

    public HandoffConfigurationBuilder StartWith(string agentId)
    {
        _startAgentId = RequireValue(agentId, nameof(agentId));
        return this;
    }

    public HandoffConfigurationBuilder Handoff(string fromAgentId, string toAgentId, string label)
    {
        _handoffs.Add(new AgentWorkflowHandoffDefinition
        {
            FromAgentId = RequireValue(fromAgentId, nameof(fromAgentId)),
            ToAgentId = RequireValue(toAgentId, nameof(toAgentId)),
            Label = RequireValue(label, nameof(label)),
            IsFallback = false
        });
        return this;
    }

    public HandoffConfigurationBuilder Fallback(
        string fromAgentId,
        string toAgentId,
        string label = "fallback")
    {
        _handoffs.Add(new AgentWorkflowHandoffDefinition
        {
            FromAgentId = RequireValue(fromAgentId, nameof(fromAgentId)),
            ToAgentId = RequireValue(toAgentId, nameof(toAgentId)),
            Label = RequireValue(label, nameof(label)),
            IsFallback = true
        });
        return this;
    }

    internal string StartAgentId =>
        string.IsNullOrWhiteSpace(_startAgentId)
            ? throw new InvalidOperationException("Workflow start agent is required.")
            : _startAgentId;

    internal IReadOnlyList<AgentWorkflowHandoffDefinition> Handoffs => _handoffs;

    private static string RequireValue(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", paramName);
        }

        return value.Trim();
    }
}
