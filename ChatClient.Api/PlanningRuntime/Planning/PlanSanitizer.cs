namespace ChatClient.Api.PlanningRuntime.Planning;

public static class PlanSanitizer
{
    public static PlanDefinition Sanitize(PlanDefinition plan, PlanModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (profile == PlanModelProfile.Draft)
            ResetRuntimeState(plan);

        return plan;
    }

    public static PlanDefinition CloneSanitized(PlanDefinition plan, PlanModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var clone = PlanJsonProfiles.Deserialize<PlanDefinition>(
            PlanJsonProfiles.SerializeToNode(plan, PlanModelProfile.Runtime),
            PlanModelProfile.Runtime)
            ?? throw new InvalidOperationException("Failed to clone plan.");

        return Sanitize(clone, profile);
    }

    private static void ResetRuntimeState(PlanDefinition plan)
    {
        foreach (var step in plan.Steps)
            PlanExecutionState.ResetStep(step);
    }
}
