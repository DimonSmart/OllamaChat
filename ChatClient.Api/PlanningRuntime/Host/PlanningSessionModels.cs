using System.Text.Json;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Domain.Models;

namespace ChatClient.Api.PlanningRuntime.Host;

public sealed class PlanningRunRequest
{
    public required ServerModel Model { get; init; }

    public required string UserQuery { get; init; }

    public required IReadOnlyCollection<string> EnabledToolNames { get; init; }
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

    public PlanDefinition? CurrentPlan { get; set; }

    public ResultEnvelope<JsonElement?>? FinalResult { get; set; }

    public List<PlanRunEvent> Events { get; } = [];

    public List<string> LogLines { get; } = [];

    public List<PlanningToolOption> AvailableTools { get; } = [];
}
