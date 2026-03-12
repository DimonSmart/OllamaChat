namespace ChatClient.Api.PlanningRuntime.Planning;

public interface IPlanner
{
    Task<PlanDefinition> CreatePlanAsync(string userQuery, CancellationToken cancellationToken = default);
}

public interface IReplanner
{
    Task<PlanDefinition> ReplanAsync(PlannerReplanRequest request, CancellationToken cancellationToken = default);
}

