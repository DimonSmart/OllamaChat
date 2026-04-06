using ChatClient.Application.Services.Agentic;

namespace ChatClient.Tests;

public sealed class WorkflowExecutionPolicyTests
{
    private readonly WorkflowExecutionPolicy _policy = new();

    [Fact]
    public void EvaluateBeforePass_StopsInteractiveWorkflowAfterFirstPass()
    {
        var decision = _policy.EvaluateBeforePass(
            new AgentWorkflowExecutionDefinition
            {
                Mode = AgentWorkflowExecutionMode.Interactive
            },
            new WorkflowExecutionProgress(StartedPasses: 1, AutomaticAssistantTurnsUsed: 0),
            completionReached: false);

        Assert.Equal(WorkflowExecutionAction.Stop, decision.Action);
        Assert.Equal(WorkflowExecutionWarning.None, decision.Warning);
    }

    [Fact]
    public void EvaluateBeforePass_StopsAutonomousWorkflowAtTurnLimitWithWarning()
    {
        var decision = _policy.EvaluateBeforePass(
            new AgentWorkflowExecutionDefinition
            {
                Mode = AgentWorkflowExecutionMode.Autonomous,
                MaxAutomaticTurns = 2,
                CompletionPhase = "complete"
            },
            new WorkflowExecutionProgress(StartedPasses: 1, AutomaticAssistantTurnsUsed: 2),
            completionReached: false);

        Assert.Equal(WorkflowExecutionAction.Stop, decision.Action);
        Assert.Equal(WorkflowExecutionWarning.StoppedWithoutCompletionMarkers, decision.Warning);
    }

    [Fact]
    public void EvaluateAfterPass_ReturnsAssistantErrorForEmptyNonTerminalPass()
    {
        var decision = _policy.EvaluateAfterPass(
            new AgentWorkflowExecutionDefinition
            {
                Mode = AgentWorkflowExecutionMode.Autonomous,
                MaxAutomaticTurns = 3
            },
            new WorkflowExecutionProgress(StartedPasses: 1, AutomaticAssistantTurnsUsed: 0),
            new WorkflowPassObservation(Ended: false, CompletedAssistantMessages: 0),
            completionReached: false);

        Assert.Equal(WorkflowExecutionAction.ShowAssistantError, decision.Action);
        Assert.Equal("Workflow returned an empty response.", decision.ErrorMessage);
    }

    [Fact]
    public void EvaluateAfterPass_WarnsWhenAutonomousWorkflowEndsWithoutCompletionMarkers()
    {
        var decision = _policy.EvaluateAfterPass(
            new AgentWorkflowExecutionDefinition
            {
                Mode = AgentWorkflowExecutionMode.Autonomous,
                MaxAutomaticTurns = 4,
                CompletionPhase = "complete",
                CompletionSummaryLabel = "final"
            },
            new WorkflowExecutionProgress(StartedPasses: 1, AutomaticAssistantTurnsUsed: 1),
            new WorkflowPassObservation(Ended: true, CompletedAssistantMessages: 1),
            completionReached: false);

        Assert.Equal(WorkflowExecutionAction.Stop, decision.Action);
        Assert.Equal(WorkflowExecutionWarning.EndedWithoutCompletionMarkers, decision.Warning);
    }

    [Fact]
    public void EvaluateAfterPass_ContinuesAutonomousWorkflowWithinBudget()
    {
        var decision = _policy.EvaluateAfterPass(
            new AgentWorkflowExecutionDefinition
            {
                Mode = AgentWorkflowExecutionMode.Autonomous,
                MaxAutomaticTurns = 4
            },
            new WorkflowExecutionProgress(StartedPasses: 1, AutomaticAssistantTurnsUsed: 1),
            new WorkflowPassObservation(Ended: false, CompletedAssistantMessages: 1),
            completionReached: false);

        Assert.Equal(WorkflowExecutionAction.Continue, decision.Action);
        Assert.Equal(WorkflowExecutionWarning.None, decision.Warning);
    }
}
