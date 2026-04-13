namespace ChatClient.Api.PlanningRuntime.Planning;

public static class PlanRuntimeHydrator
{
    public static PlanDefinition CreateRuntimePlan(PlanDefinition sourcePlan)
    {
        ArgumentNullException.ThrowIfNull(sourcePlan);

        var runtimePlan = PlanSanitizer.CloneSanitized(sourcePlan, PlanModelProfile.Runtime);
        Hydrate(runtimePlan);

        // ResultContract is [JsonIgnore] and is not preserved by JSON-based cloning.
        // Re-attach it from the source plan so the orchestrator can use it during verification.
        if (sourcePlan.ResultContract is null)
            return runtimePlan;

        return new PlanDefinition
        {
            Goal = runtimePlan.Goal,
            Steps = runtimePlan.Steps,
            ResultContract = sourcePlan.ResultContract,
            BlockedReason = runtimePlan.BlockedReason
        };
    }

    public static PlanDefinition Hydrate(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var step in plan.Steps)
            PlanExecutionState.ResetStep(step);

        return plan;
    }
}
