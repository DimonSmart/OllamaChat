namespace ChatClient.Shared.Agents;

public interface IAgentCoordinator
{
    IAgent GetNextAgent();
}
