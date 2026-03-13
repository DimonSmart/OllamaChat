using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Host;
using ChatClient.Api.PlanningRuntime.Planning;

namespace ChatClient.Api.Client.Components.Planning;

public enum PlanningVirtualNodeKind
{
    Planning,
    Replanning
}

public sealed record PlanningVirtualNodeDescriptor
{
    public const string PlanningNodeId = "__planning__";

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
}

public static class PlanningVirtualNodeProjection
{
    public static IReadOnlyList<PlanningVirtualNodeDescriptor> Build(
        PlanDefinition? plan,
        IReadOnlyList<PlanRunEvent> events,
        IReadOnlyList<PlanningToolOption> availableTools)
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
        return nodes;
    }

    public static IReadOnlyCollection<string> BuildSelectionKeys(
        PlanDefinition? plan,
        IReadOnlyList<PlanRunEvent> events,
        IReadOnlyList<PlanningToolOption> availableTools)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        if (plan is not null)
        {
            foreach (var stepId in plan.Steps.Select(step => step.Id))
            {
                keys.Add(stepId);
            }
        }

        foreach (var nodeId in Build(plan, events, availableTools).Select(node => node.Id))
        {
            keys.Add(nodeId);
        }

        return keys;
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

    private sealed class ReplanGroup(int index, ReplanStartedEvent start)
    {
        public int Index { get; } = index;

        public ReplanStartedEvent Start { get; } = start;

        public List<ReplanRoundCompletedEvent> Rounds { get; } = [];

        public List<DiagnosticPlanRunEvent> Diagnostics { get; } = [];
    }
}
