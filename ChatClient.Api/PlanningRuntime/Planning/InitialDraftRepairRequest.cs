namespace ChatClient.Api.PlanningRuntime.Planning;

public sealed class InitialDraftRepairRequest
{
    public string UserQuery { get; init; } = string.Empty;

    public int AttemptNumber { get; init; }

    public PlanDefinition DraftPlan { get; init; } = new();

    public PlanValidationIssue ValidationIssue { get; init; } = new(string.Empty, string.Empty);
}
