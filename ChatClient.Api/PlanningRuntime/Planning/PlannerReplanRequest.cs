using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Verification;

namespace ChatClient.Api.PlanningRuntime.Planning;

public sealed class PlannerReplanRequest
{
    public string UserQuery { get; init; } = string.Empty;

    public int AttemptNumber { get; init; }

    public PlanDefinition Plan { get; init; } = new();

    public ExecutionResult ExecutionResult { get; init; } = new();

    public GoalVerdict GoalVerdict { get; init; } = new();
}

