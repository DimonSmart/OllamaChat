namespace ChatClient.Application.Services.Agentic;

public sealed class AgentWorkflowExecutionDefinition
{
    public AgentWorkflowExecutionMode Mode { get; init; } = AgentWorkflowExecutionMode.Interactive;

    public int MaxAutomaticTurns { get; init; }

    public string CompletionPhase { get; init; } = "complete";

    public string? CompletionSummaryLabel { get; init; }
}

public enum AgentWorkflowExecutionMode
{
    Interactive,
    Autonomous
}
