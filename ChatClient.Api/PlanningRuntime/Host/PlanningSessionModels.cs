using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.LowLevel;
using ChatClient.Api.PlanningRuntime.Outline;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Runtime;
using ChatClient.Api.PlanningRuntime.Shared;
using ChatClient.Application.Services.Agentic;
using System.Text.Json;

namespace ChatClient.Api.PlanningRuntime.Host;

public sealed class PlanningRunRequest
{
    public required ResolvedChatAgent Planner { get; init; }

    public required string UserQuery { get; init; }
}

public sealed class PlanningToolOption
{
    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    public required string Description { get; init; }
}

public sealed class PlanningSessionState
{
    public string UserQuery { get; set; } = string.Empty;

    public bool IsRunning { get; set; }

    public bool IsCompleted { get; set; }

    public string? ActiveStepId { get; set; }

    public string? ActiveRuntimeStepId { get; set; }

    public RequestBrief? RequestBrief { get; set; }

    public OutlinePlan? OutlinePlan { get; set; }

    public string? OutlineRawResponse { get; set; }

    public LowLevelPlan? LowLevelPlan { get; set; }

    public string? LowLevelRawResponse { get; set; }

    public RuntimePlan? RuntimePlan { get; set; }

    public List<PlanningIssue> Issues { get; } = [];

    public PlanDefinition? CurrentPlan { get; set; }

    public ResultEnvelope<JsonElement?>? FinalResult { get; set; }

    public List<PlanRunEvent> Events { get; } = [];

    public List<string> LogLines { get; } = [];

    public List<PlanningToolOption> AvailableTools { get; } = [];
}
