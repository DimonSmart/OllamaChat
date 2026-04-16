using ChatClient.Api.PlanningRuntime.Outline;
using ChatClient.Api.PlanningRuntime.Shared;

namespace ChatClient.Api.PlanningRuntime.LowLevel;

public sealed class LowLevelPlanningRequest
{
    public OutlinePlan OutlinePlan { get; init; } = new();

    public IReadOnlyCollection<PlannerCapabilitySummary> Capabilities { get; init; } = [];
}

public interface ILowLevelPlanner
{
    Task<PlanningStageResult<LowLevelPlan>> CreatePlanAsync(
        LowLevelPlanningRequest request,
        CancellationToken cancellationToken = default);
}
