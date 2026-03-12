using System.Text.Json;
using ChatClient.Api.PlanningRuntime.Common;

namespace ChatClient.Api.PlanningRuntime.Execution;

public sealed record StepExecutionTrace
{
    public string StepId { get; init; } = string.Empty;

    public bool Success { get; init; }

    public bool Reused { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public JsonElement? ErrorDetails { get; init; }

    public List<JsonElement> Calls { get; init; } = new();

    public List<StepVerificationIssue> VerificationIssues { get; init; } = [];
}

public sealed record StepVerificationIssue
{
    public string Code { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

public sealed class ExecutionResult
{
    public List<StepExecutionTrace> StepTraces { get; init; } = new();

    public bool HasErrors => StepTraces.Any(x => !x.Success);

    public bool HasVerificationIssues => StepTraces.Any(x => x.VerificationIssues.Count > 0);

    public ResultEnvelope<JsonElement?>? LastEnvelope { get; init; }
}

