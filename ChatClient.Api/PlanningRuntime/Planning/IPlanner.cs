namespace ChatClient.Api.PlanningRuntime.Planning;

public interface IPlanner
{
    Task<PlanDefinition> CreatePlanAsync(string userQuery, CancellationToken cancellationToken = default);
}

public interface IPlanningDraftPlanner
{
    Task<PlanDefinition> CreatePlanAsync(
        PlanningDraftPlannerRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class PlanningDraftPlannerRequest
{
    public required string OriginalUserQuery { get; init; }

    public required string PlannerInput { get; init; }
}

public interface IPlanningRequestAnalyzer
{
    Task<PlanningRequestAnalysis> AnalyzeAsync(
        string userQuery,
        CancellationToken cancellationToken = default);
}

public interface IReplanner
{
    Task<PlanDefinition> ReplanAsync(PlannerReplanRequest request, CancellationToken cancellationToken = default);
}

public interface IInitialDraftRepairer
{
    Task<PlanDefinition> RepairAsync(InitialDraftRepairRequest request, CancellationToken cancellationToken = default);
}

