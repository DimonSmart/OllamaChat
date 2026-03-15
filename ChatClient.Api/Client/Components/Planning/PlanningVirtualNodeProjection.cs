using System.Text.Json;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Host;
using ChatClient.Api.PlanningRuntime.Planning;

namespace ChatClient.Api.Client.Components.Planning;

public enum PlanningVirtualNodeKind
{
    Planning,
    Replanning,
    Result
}

public sealed record PlanningVirtualNodeDescriptor
{
    public const string PlanningNodeId = "__planning__";
    public const string ResultNodeId = "__result__";

    public required string Id { get; init; }

    public required PlanningVirtualNodeKind Kind { get; init; }

    public required string Title { get; init; }

    public required string Subtitle { get; init; }

    public required string StatusValue { get; init; }

    public string? Summary { get; init; }

    public PlanningAttemptStartedEvent? PlanningStarted { get; init; }

    public PlanDefinition? PlannedDraft { get; init; }

    public IReadOnlyList<PlanningToolOption> AvailableTools { get; init; } = [];

    public IReadOnlyList<DiagnosticPlanRunEvent> Diagnostics { get; init; } = [];

    public ReplanStartedEvent? ReplanStarted { get; init; }

    public IReadOnlyList<ReplanRoundCompletedEvent> ReplanRounds { get; init; } = [];

    public ResultEnvelope<JsonElement?>? FinalResult { get; init; }
}

public static class PlanningVirtualNodeProjection
{
    public static IReadOnlyList<PlanningVirtualNodeDescriptor> Build(
        PlanDefinition? plan,
        IReadOnlyList<PlanRunEvent> events,
        IReadOnlyList<PlanningToolOption> availableTools,
        ResultEnvelope<JsonElement?>? finalResult)
    {
        var nodes = new List<PlanningVirtualNodeDescriptor>();

        var planningStart = events
            .OfType<PlanningAttemptStartedEvent>()
            .FirstOrDefault(evt => string.Equals(evt.Phase, "plan", StringComparison.OrdinalIgnoreCase));
        var createdPlan = events
            .OfType<PlanCreatedEvent>()
            .LastOrDefault(evt => string.Equals(evt.Phase, "plan", StringComparison.OrdinalIgnoreCase))
            ?.Plan
            ?? plan;
        var plannerDiagnostics = events
            .OfType<DiagnosticPlanRunEvent>()
            .Where(evt => string.Equals(evt.Source, "planner", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (planningStart is not null || createdPlan is not null || availableTools.Count > 0)
        {
            nodes.Add(new PlanningVirtualNodeDescriptor
            {
                Id = PlanningVirtualNodeDescriptor.PlanningNodeId,
                Kind = PlanningVirtualNodeKind.Planning,
                Title = "planning",
                Subtitle = availableTools.Count > 0
                    ? $"tools: {availableTools.Count}"
                    : "initial plan generation",
                StatusValue = createdPlan is null ? "running" : "done",
                Summary = createdPlan is null
                    ? "Waiting for planner output."
                    : $"steps: {createdPlan.Steps.Count}",
                PlanningStarted = planningStart,
                PlannedDraft = createdPlan,
                AvailableTools = availableTools.ToList(),
                Diagnostics = plannerDiagnostics
            });
        }

        nodes.AddRange(BuildReplanNodes(events));
        var resultNode = BuildResultNode(finalResult);
        if (resultNode is not null)
        {
            nodes.Add(resultNode);
        }

        return nodes;
    }

    public static IReadOnlyCollection<string> BuildSelectionKeys(
        PlanDefinition? plan,
        IReadOnlyList<PlanRunEvent> events,
        IReadOnlyList<PlanningToolOption> availableTools,
        ResultEnvelope<JsonElement?>? finalResult)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        if (plan is not null)
        {
            foreach (var stepId in plan.Steps.Select(step => step.Id))
            {
                keys.Add(stepId);
            }
        }

        foreach (var nodeId in Build(plan, events, availableTools, finalResult).Select(node => node.Id))
        {
            keys.Add(nodeId);
        }

        if (plan is not null)
        {
            foreach (var linkId in PlanningGraphLinkProjection.Build(plan.Steps, finalResult).Select(link => link.Id))
            {
                keys.Add(linkId);
            }
        }

        return keys;
    }

    public static string? ResolveDefaultSelectionId(
        PlanDefinition? plan,
        IReadOnlyList<PlanRunEvent> events,
        IReadOnlyList<PlanningToolOption> availableTools,
        ResultEnvelope<JsonElement?>? finalResult,
        string? activeStepId)
    {
        var validNodeIds = BuildSelectionKeys(plan, events, availableTools, finalResult);
        if (validNodeIds.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(activeStepId) && validNodeIds.Contains(activeStepId))
        {
            return activeStepId;
        }

        if (finalResult is not null && validNodeIds.Contains(PlanningVirtualNodeDescriptor.ResultNodeId))
        {
            return PlanningVirtualNodeDescriptor.ResultNodeId;
        }

        return Build(plan, events, availableTools, finalResult).FirstOrDefault()?.Id
            ?? plan?.Steps.FirstOrDefault()?.Id;
    }

    private static IReadOnlyList<PlanningVirtualNodeDescriptor> BuildReplanNodes(IReadOnlyList<PlanRunEvent> events)
    {
        var groups = new List<ReplanGroup>();
        ReplanGroup? current = null;

        foreach (var planRunEvent in events)
        {
            switch (planRunEvent)
            {
                case ReplanStartedEvent started:
                    if (current is not null)
                    {
                        groups.Add(current);
                    }

                    current = new ReplanGroup(groups.Count + 1, started);
                    break;

                case ReplanRoundCompletedEvent roundCompleted when current is not null:
                    current.Rounds.Add(roundCompleted);
                    break;

                case DiagnosticPlanRunEvent diagnostic when current is not null
                    && string.Equals(diagnostic.Source, "replanner", StringComparison.OrdinalIgnoreCase):
                    current.Diagnostics.Add(diagnostic);
                    break;
            }
        }

        if (current is not null)
        {
            groups.Add(current);
        }

        return groups
            .Select(group => new PlanningVirtualNodeDescriptor
            {
                Id = $"__replan__:{group.Index}",
                Kind = PlanningVirtualNodeKind.Replanning,
                Title = $"replanning #{group.Index}",
                Subtitle = $"after attempt {group.Start.Request.AttemptNumber} | rounds: {group.Rounds.Count}",
                StatusValue = group.Rounds.LastOrDefault()?.Done == true ? "done" : "running",
                Summary = Shorten(group.Start.Request.GoalVerdict.Reason, 96),
                ReplanStarted = group.Start,
                ReplanRounds = group.Rounds.ToList(),
                Diagnostics = group.Diagnostics.ToList()
            })
            .ToList();
    }

    private static PlanningVirtualNodeDescriptor? BuildResultNode(ResultEnvelope<JsonElement?>? finalResult)
    {
        if (finalResult is null)
        {
            return null;
        }

        return new PlanningVirtualNodeDescriptor
        {
            Id = PlanningVirtualNodeDescriptor.ResultNodeId,
            Kind = PlanningVirtualNodeKind.Result,
            Title = "result",
            Subtitle = finalResult.Ok
                ? "ok: true"
                : $"error: {finalResult.Error?.Code ?? "planning_failed"}",
            StatusValue = finalResult.Ok
                ? PlanStepStatuses.Done
                : PlanStepStatuses.Fail,
            Summary = BuildResultSummary(finalResult),
            FinalResult = CloneEnvelope(finalResult)
        };
    }

    private static string Shorten(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maxLength
            ? normalized
            : $"{normalized[..maxLength]}...";
    }

    private static string BuildResultSummary(ResultEnvelope<JsonElement?> result)
    {
        if (!result.Ok)
        {
            return Shorten(result.Error?.Message ?? result.Error?.Code ?? "Planning failed.", 96);
        }

        if (TryExtractSummary(result.Data, out var summary))
        {
            return Shorten(summary, 96);
        }

        return "Final result is available.";
    }

    private static bool TryExtractSummary(JsonElement? data, out string summary)
    {
        summary = string.Empty;
        if (data is not { } value || value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return false;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                summary = value.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(summary);

            case JsonValueKind.Object:
                foreach (var fieldName in new[] { "summary", "answer", "result", "message", "text", "content" })
                {
                    if (value.TryGetProperty(fieldName, out var property) &&
                        property.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(property.GetString()))
                    {
                        summary = property.GetString()!;
                        return true;
                    }
                }

                summary = "Structured JSON result.";
                return true;

            case JsonValueKind.Array:
                summary = $"items: {value.GetArrayLength()}";
                return true;

            default:
                summary = value.GetRawText();
                return true;
        }
    }

    private static ResultEnvelope<JsonElement?> CloneEnvelope(ResultEnvelope<JsonElement?> result) =>
        result.Ok
            ? ResultEnvelope<JsonElement?>.Success(result.Data?.Clone())
            : ResultEnvelope<JsonElement?>.Failure(
                result.Error?.Code ?? "planning_failed",
                result.Error?.Message ?? "Planning failed.",
                result.Error?.Details?.Clone());

    private sealed class ReplanGroup(int index, ReplanStartedEvent start)
    {
        public int Index { get; } = index;

        public ReplanStartedEvent Start { get; } = start;

        public List<ReplanRoundCompletedEvent> Rounds { get; } = [];

        public List<DiagnosticPlanRunEvent> Diagnostics { get; } = [];
    }
}
