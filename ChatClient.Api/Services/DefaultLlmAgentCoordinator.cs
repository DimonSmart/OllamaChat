using ChatClient.Shared.LlmAgents;

namespace ChatClient.Api.Services;

/// <summary>
/// Basic agent coordinator that always returns a single agent instance.
/// </summary>
public class DefaultLlmAgentCoordinator(ILlmAgent agent) : ILlmAgentCoordinator
{
    private readonly ILlmAgent _agent = agent;

    public ILlmAgent GetNextAgent() => _agent;

    public bool ShouldContinueConversation(int cycleCount) => false;
}
