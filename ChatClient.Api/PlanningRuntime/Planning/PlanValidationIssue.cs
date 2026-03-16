namespace ChatClient.Api.PlanningRuntime.Planning;

public sealed record PlanValidationIssue(
    string Code,
    string Message,
    string? StepId = null,
    string? InputName = null,
    string? ToolName = null,
    string? BindingFrom = null,
    string? SourceStepId = null,
    string? Path = null,
    string? Expected = null,
    string? Actual = null);

public sealed class PlanValidationException(PlanValidationIssue issue) : InvalidOperationException(issue.Message)
{
    public PlanValidationIssue Issue { get; } = issue;
}
