namespace ChatClient.Shared.LlmAgents;

public interface ILlmAgentCoordinator
{
    ILlmAgent GetNextAgent();

    /// <summary>
    /// Determines if agents should continue exchanging messages without user input.
    /// </summary>
    /// <param name="cycleCount">Number of consecutive agent messages since the last user input.</param>
    bool ShouldContinueConversation(int cycleCount);
}
