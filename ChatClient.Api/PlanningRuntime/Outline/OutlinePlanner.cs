using ChatClient.Api.PlanningRuntime.Shared;

namespace ChatClient.Api.PlanningRuntime.Outline;

public sealed class OutlinePlanningRequest
{
    public string UserQuery { get; init; } = string.Empty;

    public string ResultExpectations { get; init; } = string.Empty;

    public IReadOnlyCollection<PlannerCapabilitySummary> Capabilities { get; init; } = [];
}

public interface IOutlinePlanner
{
    Task<PlanningStageResult<OutlinePlan>> CreatePlanAsync(
        OutlinePlanningRequest request,
        CancellationToken cancellationToken = default);
}
