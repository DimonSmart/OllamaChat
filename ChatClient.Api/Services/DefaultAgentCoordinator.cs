using ChatClient.Shared.Agents;

namespace ChatClient.Api.Services;

/// <summary>
/// Basic agent coordinator that always returns a single agent instance.
/// </summary>
public class DefaultAgentCoordinator(IAgent agent) : IAgentCoordinator
{
    private readonly IAgent _agent = agent;

    public IAgent GetNextAgent() => _agent;
}
