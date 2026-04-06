namespace ChatClient.Application.Services.Agentic;

public interface IWorkflowExecutionPolicy
{
    WorkflowExecutionDecision EvaluateBeforePass(
        AgentWorkflowExecutionDefinition execution,
        WorkflowExecutionProgress progress,
        bool completionReached);

    WorkflowExecutionDecision EvaluateAfterPass(
        AgentWorkflowExecutionDefinition execution,
        WorkflowExecutionProgress progress,
        WorkflowPassObservation pass,
        bool completionReached);
}

public sealed class WorkflowExecutionPolicy : IWorkflowExecutionPolicy
{
    public WorkflowExecutionDecision EvaluateBeforePass(
        AgentWorkflowExecutionDefinition execution,
        WorkflowExecutionProgress progress,
        bool completionReached)
    {
        ArgumentNullException.ThrowIfNull(execution);
        Validate(progress);

        if (execution.Mode == AgentWorkflowExecutionMode.Autonomous && completionReached)
        {
            return WorkflowExecutionDecision.Stop();
        }

        if (execution.Mode == AgentWorkflowExecutionMode.Interactive && progress.StartedPasses > 0)
        {
            return WorkflowExecutionDecision.Stop();
        }

        if (execution.Mode == AgentWorkflowExecutionMode.Autonomous &&
            progress.AutomaticAssistantTurnsUsed >= execution.MaxAutomaticTurns)
        {
            return new WorkflowExecutionDecision(
                WorkflowExecutionAction.Stop,
                HasCompletionMarkers(execution)
                    ? WorkflowExecutionWarning.StoppedWithoutCompletionMarkers
                    : WorkflowExecutionWarning.None);
        }

        return WorkflowExecutionDecision.Continue();
    }

    public WorkflowExecutionDecision EvaluateAfterPass(
        AgentWorkflowExecutionDefinition execution,
        WorkflowExecutionProgress progress,
        WorkflowPassObservation pass,
        bool completionReached)
    {
        ArgumentNullException.ThrowIfNull(execution);
        Validate(progress);
        Validate(pass);

        if (pass.CompletedAssistantMessages == 0)
        {
            if (pass.Ended)
            {
                return WorkflowExecutionDecision.Stop();
            }

            if (execution.Mode == AgentWorkflowExecutionMode.Autonomous && completionReached)
            {
                return WorkflowExecutionDecision.Stop();
            }

            return WorkflowExecutionDecision.AssistantError("Workflow returned an empty response.");
        }

        if (execution.Mode == AgentWorkflowExecutionMode.Autonomous && pass.Ended)
        {
            return new WorkflowExecutionDecision(
                WorkflowExecutionAction.Stop,
                !completionReached && HasCompletionMarkers(execution)
                    ? WorkflowExecutionWarning.EndedWithoutCompletionMarkers
                    : WorkflowExecutionWarning.None);
        }

        if (execution.Mode == AgentWorkflowExecutionMode.Autonomous &&
            progress.AutomaticAssistantTurnsUsed > execution.MaxAutomaticTurns)
        {
            return WorkflowExecutionDecision.ThrowExceededAutomaticTurns();
        }

        if (execution.Mode != AgentWorkflowExecutionMode.Autonomous)
        {
            return WorkflowExecutionDecision.Stop();
        }

        if (completionReached)
        {
            return WorkflowExecutionDecision.Stop();
        }

        if (progress.AutomaticAssistantTurnsUsed >= execution.MaxAutomaticTurns)
        {
            return new WorkflowExecutionDecision(
                WorkflowExecutionAction.Stop,
                HasCompletionMarkers(execution)
                    ? WorkflowExecutionWarning.StoppedWithoutCompletionMarkers
                    : WorkflowExecutionWarning.None);
        }

        return WorkflowExecutionDecision.Continue();
    }

    private static bool HasCompletionMarkers(AgentWorkflowExecutionDefinition execution) =>
        !string.IsNullOrWhiteSpace(execution.CompletionPhase) ||
        !string.IsNullOrWhiteSpace(execution.CompletionSummaryLabel);

    private static void Validate(WorkflowExecutionProgress progress)
    {
        if (progress.StartedPasses < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(progress.StartedPasses));
        }

        if (progress.AutomaticAssistantTurnsUsed < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(progress.AutomaticAssistantTurnsUsed));
        }
    }

    private static void Validate(WorkflowPassObservation pass)
    {
        if (pass.CompletedAssistantMessages < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pass.CompletedAssistantMessages));
        }
    }
}

public readonly record struct WorkflowExecutionProgress(
    int StartedPasses,
    int AutomaticAssistantTurnsUsed);

public readonly record struct WorkflowPassObservation(
    bool Ended,
    int CompletedAssistantMessages);

public readonly record struct WorkflowExecutionDecision(
    WorkflowExecutionAction Action,
    WorkflowExecutionWarning Warning = WorkflowExecutionWarning.None,
    string? ErrorMessage = null)
{
    public static WorkflowExecutionDecision Continue() => new(WorkflowExecutionAction.Continue);

    public static WorkflowExecutionDecision Stop() => new(WorkflowExecutionAction.Stop);

    public static WorkflowExecutionDecision AssistantError(string message) =>
        new(WorkflowExecutionAction.ShowAssistantError, ErrorMessage: message);

    public static WorkflowExecutionDecision ThrowExceededAutomaticTurns() =>
        new(WorkflowExecutionAction.ThrowExceededAutomaticTurns);
}

public enum WorkflowExecutionAction
{
    Continue,
    Stop,
    ShowAssistantError,
    ThrowExceededAutomaticTurns
}

public enum WorkflowExecutionWarning
{
    None,
    EndedWithoutCompletionMarkers,
    StoppedWithoutCompletionMarkers
}
