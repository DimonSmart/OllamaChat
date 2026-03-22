using ChatClient.Application.Services.Agentic;

namespace ChatClient.Api.PlanningRuntime.Agents;

public sealed class PlanningCallableAgentDescriptor
{
    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    public required string Description { get; init; }

    public required ResolvedChatAgent Agent { get; init; }
}

public sealed class PlanningCallableAgentCatalog
{
    public static PlanningCallableAgentCatalog Empty { get; } = new([]);

    private readonly IReadOnlyList<PlanningCallableAgentDescriptor> _agents;
    private readonly IReadOnlyDictionary<string, PlanningCallableAgentDescriptor> _agentsByName;

    public PlanningCallableAgentCatalog(IReadOnlyCollection<PlanningCallableAgentDescriptor> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);

        _agents = agents
            .OrderBy(static agent => agent.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _agentsByName = _agents.ToDictionary(agent => agent.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<PlanningCallableAgentDescriptor> ListAgents() => _agents;

    public bool TryGet(string name, out PlanningCallableAgentDescriptor descriptor) =>
        _agentsByName.TryGetValue(name, out descriptor!);

    public PlanningCallableAgentDescriptor GetRequired(string name)
    {
        if (TryGet(name, out var descriptor))
            return descriptor;

        throw new KeyNotFoundException($"Callable agent '{name}' was not found.");
    }
}
