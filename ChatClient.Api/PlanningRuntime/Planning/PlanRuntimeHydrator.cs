namespace ChatClient.Api.PlanningRuntime.Planning;

public static class PlanRuntimeHydrator
{
    public static PlanDefinition CreateRuntimePlan(PlanDefinition sourcePlan)
    {
        ArgumentNullException.ThrowIfNull(sourcePlan);

        var runtimePlan = PlanSanitizer.CloneSanitized(sourcePlan, PlanModelProfile.Runtime);
        Hydrate(runtimePlan);
        return runtimePlan;
    }

    public static PlanDefinition Hydrate(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var step in plan.Steps)
            PlanExecutionState.ResetStep(step);

        return plan;
    }
}
