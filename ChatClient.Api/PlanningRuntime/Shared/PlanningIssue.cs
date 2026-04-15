namespace ChatClient.Api.PlanningRuntime.Shared;

public sealed class PlanningIssue
{
    public string Code { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;
}
