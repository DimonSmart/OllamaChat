namespace ChatClient.Api.PlanningRuntime.Planning;

public static class PlanExecutionState
{
    public static bool IsDone(PlanStep step) =>
        string.Equals(step.Status, PlanStepStatuses.Done, StringComparison.Ordinal) && step.Error is null;

    public static bool IsFailed(PlanStep step) =>
        string.Equals(step.Status, PlanStepStatuses.Fail, StringComparison.Ordinal);

    public static void ResetStep(PlanStep step)
    {
        step.Status = PlanStepStatuses.Todo;
        step.Result = null;
        step.Error = null;
    }

    public static void ResetFrom(PlanDefinition plan, int stepIndex)
    {
        for (var index = stepIndex; index < plan.Steps.Count; index++)
            ResetStep(plan.Steps[index]);
    }
}

